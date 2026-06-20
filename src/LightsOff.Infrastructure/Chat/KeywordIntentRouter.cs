using System.Globalization;
using System.Text;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Chat;

/// <summary>
/// Deterministic, dependency-free <see cref="IIntentRouter"/>. It maps a free-text chat message
/// onto a <see cref="ChatIntent"/> using a small, ordered keyword table (Portuguese + English),
/// then checks the live <see cref="WorkflowState"/> so it only marks an intent
/// <see cref="IntentClassification.Actionable"/> when the pipeline actually allows it.
///
/// This is the always-available fallback for the unified ticket chat — no LLM round-trip, no
/// network, fully testable. A future LLM-backed router can wrap or replace it for fuzzier phrasing;
/// the contract is identical.
/// </summary>
public sealed class KeywordIntentRouter : IIntentRouter
{
    // Multi-word entries are matched as substrings of the normalized message; single tokens are
    // matched by exact-or-prefix on the message's word tokens (prefix only when length >= 4, to
    // keep short tokens like "pr" from over-matching). Order matters: first hit wins.
    private static readonly (ChatIntent Intent, string[] Keywords)[] Rules =
    {
        (ChatIntent.Cancel,            new[] { "cancel", "cancela", "cancelar", "abort", "aborta", "abortar", "stop", "interrompe", "interromper" }),
        (ChatIntent.RunAutonomous,     new[] { "autonom", "hands-off", "faz tudo", "faca tudo", "roda tudo", "rode tudo", "ponta a ponta", "end to end", "do everything", "automatico", "automatic" }),
        (ChatIntent.AddressPrFindings, new[] { "address", "enderec", "resolve", "resolva", "resolver", "responde os coment", "responder os coment", "reply to", "fix the comments", "aplica os finding", "aplicar os finding", "trata os coment", "resolve thread", "resolver thread", "marca como resolv", "mark resolved" }),
        (ChatIntent.ReviewPr,          new[] { "review pr", "revisar pr", "revisao da pr", "review da pr", "comentarios da pr", "pr comments", "pr feedback", "findings", "olha a pr", "checa a pr", "ver a pr", "ver os coment" }),
        (ChatIntent.ApprovePlan,       new[] { "aprov", "approve", "approved", "lgtm", "looks good", "pode seguir", "ta bom o plano", "plano ok", "ok o plano", "ship it" }),
        (ChatIntent.Implement,         new[] { "implement", "implementa", "implementar", "implemente", "codifica", "codificar", "escreve o codigo", "escrever o codigo", "build it", "code it", "manda ver", "comeca a implement", "start coding", "faz a implement", "fazer a implement" }),
        (ChatIntent.RunTests,          new[] { "test", "testa", "testar", "teste", "rode os teste", "rodar os teste", "run test", "valida", "validar", "validate", "check the test" }),
        (ChatIntent.Submit,            new[] { "abre o pr", "abrir o pr", "abre pr", "open the pr", "open a pr", "open pr", "submit", "pull request", "manda o pr", "mandar o pr", "envia o pr", "enviar o pr", "push", "create pr", "criar pr", "cria o pr" }),
        (ChatIntent.ReworkPlan,        new[] { "rework", "refaz o plano", "refazer o plano", "muda o plano", "mudar o plano", "change the plan", "ajusta o plano", "ajustar o plano", "replan", "revise the plan", "altera o plano", "alterar o plano", "atualiza o plano" }),
        (ChatIntent.Analyze,           new[] { "analyze", "analisa", "analisar", "reanalisa", "re-analyze", "analisar de novo", "analise novamente" }),
    };

    public Task<IntentClassification> ClassifyAsync(
        string message,
        WorkflowState state,
        bool hasOpenQuestions,
        CancellationToken ct = default)
        => Task.FromResult(Classify(message, state, hasOpenQuestions));

    /// <summary>Synchronous core — pure function, exposed for direct testing.</summary>
    public static IntentClassification Classify(string message, WorkflowState state, bool hasOpenQuestions)
    {
        var payload = (message ?? string.Empty).Trim();
        var normalized = Normalize(payload);

        if (string.IsNullOrWhiteSpace(normalized))
            return new IntentClassification(ChatIntent.Chat, 0.5, Payload: payload);

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var (intent, keywords) in Rules)
        {
            if (!Matches(normalized, tokens, keywords)) continue;

            var (actionable, reason) = CanRun(intent, state, hasOpenQuestions);
            return new IntentClassification(
                intent,
                Confidence: actionable ? 0.85 : 0.4,
                Rationale: reason,
                Payload: payload,
                Actionable: actionable);
        }

        // No explicit command. If the agent asked something, treat free text as the answer.
        if (hasOpenQuestions)
            return new IntentClassification(ChatIntent.AnswerQuestion, 0.7, Payload: payload);

        // Otherwise it's conversation. The caller's existing default (refine analysis / discuss the
        // plan) takes over from here.
        return new IntentClassification(ChatIntent.Chat, 0.5, Payload: payload);
    }

    /// <summary>
    /// Gate an intent against the live workflow. Returns whether it can run now and, when it can't,
    /// a short reason the UI can show in the chat.
    /// </summary>
    private static (bool Actionable, string? Reason) CanRun(ChatIntent intent, WorkflowState state, bool hasOpenQuestions)
    {
        bool planExists = state.Plan is not null;
        bool planApproved = state.Plan?.IsApproved == true;
        bool hasOpenPr = state.SubmitResults?.Any(r => !string.IsNullOrEmpty(r.Url)) == true;

        return intent switch
        {
            ChatIntent.Analyze        => (true, null),
            ChatIntent.Chat           => (true, null),
            ChatIntent.Cancel         => (true, null),
            ChatIntent.AnswerQuestion => hasOpenQuestions
                ? (true, null)
                : (false, "There are no open questions to answer right now."),
            ChatIntent.RefinePlan     => state.Analysis is not null
                ? (true, null)
                : (false, "I need to analyze the ticket before refining anything."),
            ChatIntent.ReworkPlan     => planExists
                ? (true, null)
                : (false, "There's no plan to rework yet."),
            ChatIntent.ApprovePlan    => planExists && !planApproved
                ? (true, null)
                : planApproved
                    ? (false, "The plan is already approved.")
                    : (false, "There's no plan to approve yet."),
            ChatIntent.Implement      => planApproved
                ? (true, null)
                : (false, "Approve the plan first, then I can implement it."),
            ChatIntent.RunAutonomous  => planApproved
                ? (true, null)
                : (false, "Approve the plan first, then I can run the autonomous chain."),
            ChatIntent.RunTests       => state.IsStepUnlocked(PipelineStep.Validate)
                ? (true, null)
                : (false, "Run the implementation step before testing."),
            ChatIntent.Submit         => state.IsStepUnlocked(PipelineStep.Submit) || state.IsStepUnlocked(PipelineStep.Validate)
                ? (true, null)
                : (false, "Implement the change before opening a PR."),
            ChatIntent.ReviewPr or ChatIntent.AddressPrFindings => hasOpenPr
                ? (true, null)
                : (false, "There's no open pull request to review yet."),
            _ => (true, null),
        };
    }

    private static bool Matches(string normalized, string[] tokens, string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (kw.Contains(' '))
            {
                if (normalized.Contains(kw, StringComparison.Ordinal)) return true;
            }
            else
            {
                foreach (var tok in tokens)
                {
                    if (tok == kw) return true;
                    if (kw.Length >= 4 && tok.StartsWith(kw, StringComparison.Ordinal)) return true;
                }
            }
        }
        return false;
    }

    /// <summary>Lowercase, strip diacritics, collapse non-alphanumerics to spaces.</summary>
    private static string Normalize(string input)
    {
        var lowered = input.ToLowerInvariant();
        var decomposed = lowered.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch == '-') sb.Append('-'); // keep hyphen for tokens like "hands-off"/"re-analyze"
            else sb.Append(' ');
        }
        // Collapse runs of whitespace so phrase substrings match regardless of original punctuation.
        var parts = sb.ToString().Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
