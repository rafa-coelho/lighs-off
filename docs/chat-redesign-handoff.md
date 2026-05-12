# Chat redesign — handoff

## Why this doc exists

Mid-flight UX rework on the **Understand & Plan** step. Pausing to add another agent backend (Copilot CLI). This file captures exactly where we stopped so the redesign can be picked up cleanly later.

## What the user asked for (the unmet ask)

From the most recent feedback session:

1. **Single chat surface, no separate plan card above.** The current layout has TWO blocks — a plan card on top, a chat thread below. The user wants ONE block (chat) with the plan embedded as an inline attachment.
2. **Plan as a clickable card inside the chat.** When the agent generates or reworks a plan, it should appear as a card-style attachment inside the agent's chat message — like Claude.com's "Document · MD" attachment with the Google Drive icon + Download button, or the artifact pattern.
3. **Click the card → opens a side panel.** Like Claude.com's right-side viewer that slides in. The chat stays visible on the left; the panel renders the full plan as markdown on the right. Click X (or backdrop) to close.
4. **Chat always says something.** Every action should produce a narrative agent message — "Got it, analyzing your repo…", "Found 3 things I need clarified before drafting a plan", "Drafting now…", "Here's the plan: 8 tasks across 4 files [PlanCard]", "Updated the plan: shifted 3 tasks to use Prisma [PlanCard]". The chat should feel like a conversation with a thinking partner, not a transactional form.
5. **Richer visual components in chat.** Loading spinners inside agent messages, code blocks (already work via markdown), quotes, links, and mentions of charts/graphs. The user listed "loading, code blocks, quotes, links, charts, graphs" — most are markdown-native, but charts/graphs would need a charting library if pursued.

Direct user quote: *"I don't like having two blocks on the UI. Merge it up: The plan should be inside the chat and it can be like a card that I click and it opens aside, but always saying something on the chat. And the chat should be really visual having components like loading, code blocks, quotes, links, charts, graphs… etc."*

## Current state of the code (what shipped before pausing)

- ✅ **Single-column layout** — [TicketDetail.razor:RenderPlanStep](../src/LightsOff.App/Components/Pages/TicketDetail.razor) is now max-w-3xl centered, no two-column grid.
- ✅ **Plan rendered as markdown** — Markdig pipeline in [MarkdownRenderer.cs](../src/LightsOff.App/Services/MarkdownRenderer.cs); `.md-content` styles in [app.css](../src/LightsOff.App/wwwroot/app.css).
- ✅ **Pre-flight gating** — analyze runs first; if there are blocking questions, the **plan does not generate** until the wizard is answered. ([TicketDetail.razor:RunAnalysisAsync + GeneratePlanFromAnalysisAsync])
- ✅ **Question wizard modal** — [QuestionWizard.razor](../src/LightsOff.App/Components/Shared/QuestionWizard.razor) — sequential, one-at-a-time, Skip / Back / Next / Done, ⌘+Enter, Esc cancel, progress bar.
- ✅ **ChatGPT-style chat thread** — [ChatThread.razor](../src/LightsOff.App/Components/Shared/ChatThread.razor) — full-width rows, avatars, name + time, markdown for agent replies, Enter-to-send / Shift+Enter for newline, circular send button.
- ✅ **Approve gated on no-pending-questions** — Approve button is disabled if any open questions remain.

## What's still missing — the actual work to resume with

### 1. ChatMessage attachment metadata

`ChatMessage` is currently `record(Role, Content, At)`. Needs an optional `Attachment` field so an agent message can carry a plan-card payload.

Location: [TicketAnalysis.cs](../src/LightsOff.Core/Domain/TicketAnalysis.cs).

Suggested shape:
```csharp
public record ChatMessage(string Role, string Content, DateTimeOffset At, ChatAttachment? Attachment = null) {
    public const string RoleUser = "user";
    public const string RoleAgent = "agent";
    public const string RoleSystem = "system";
}

public record ChatAttachment(
    string Kind,           // e.g. "plan", "diff", "test-report"
    string Title,          // e.g. "Implementation plan v3"
    string Subtitle,       // e.g. "8 tasks · 4 file changes"
    string PayloadRef);    // opaque id; for plans, just "current"
```

Persisted via System.Text.Json in `WorkflowStateJson`. Existing messages default to `Attachment = null`.

### 2. PlanCard.razor — the inline attachment in chat

A clickable card rendered inside an agent's chat message, similar visually to Claude.com's "Document · MD" card.

Visual:
```
┌───────────────────────────────────────┐
│ 📄  Implementation plan v3            │
│     8 tasks · 4 file changes          │
└───────────────────────────────────────┘
```

Click → fires `OnOpen` callback that the parent uses to set the side panel state.

Location: `src/LightsOff.App/Components/Shared/PlanCard.razor` (new).

### 3. SidePanel.razor — slide-in drawer

A right-side drawer that slides in over (or beside) the chat. Renders the current plan as markdown inside.

Visual: takes ~40-50% of the viewport on desktop, full-screen on mobile. Backdrop on mobile, push-aside on desktop. Header with title + Copy + close X.

Location: `src/LightsOff.App/Components/Shared/SidePanel.razor` (new).

CSS — needs entries in [app.css](../src/LightsOff.App/wwwroot/app.css):
```css
@keyframes side-panel-in {
    from { transform: translateX(100%); }
    to   { transform: translateX(0); }
}
.side-panel-in { animation: side-panel-in 200ms ease-out; }
```

### 4. ChatThread.razor — render attachments inside agent messages

[ChatThread.razor](../src/LightsOff.App/Components/Shared/ChatThread.razor) currently renders message body only. After the markdown body, if `m.Attachment is not null`, render a `<PlanCard ... OnOpen=... />`.

Need to expose an `OnAttachmentClick` callback as a parameter so the parent (`TicketDetail`) can hook the side panel state.

### 5. TicketDetail.razor — drop the standalone plan card; produce narrative agent messages

Concrete edits:

- **Remove** the standalone plan-rendering `<article>` block above the `ChatThread` in [RenderPlanStep](../src/LightsOff.App/Components/Pages/TicketDetail.razor).
- **Inject narrative agent messages** at every workflow event:
  - User clicks Start → analyze begins → append `ChatMessage(Agent, "Analyzing the ticket and your repos…")` to `_analysis.Conversation`.
  - Analyze finishes with no blocking questions → append `ChatMessage(Agent, "Analysis complete. Drafting a plan now…")`.
  - Analyze surfaces N blocking questions → append `ChatMessage(Agent, "I found N things I need clarified before drafting a plan. Click 'Answer' to walk through them.")` (don't auto-open the wizard — let the user click).
  - Plan generates → append `ChatMessage(Agent, "Here's the plan based on your input.", Attachment: ChatAttachment(Kind="plan", Title="Implementation plan", Subtitle=$"{tasks} tasks · {changes} file changes", PayloadRef="current"))`.
  - Plan reworks → append `ChatMessage(Agent, BuildPlanDeltaSummary(...), Attachment: ChatAttachment(...))`.
  - Wizard completes → append `ChatMessage(Agent, "Got your answers. Reworking the plan…")`.
- **Wire side panel state**: `private bool _planPanelOpen; private string _planPanelTitle;` + handler `OnPlanCardClicked` → sets `_planPanelOpen = true`.
- **Render the side panel** at the bottom of `RenderPlanStep`:
  ```razor
  <SidePanel @bind-Open="_planPanelOpen" Title="Implementation plan">
      <article class="md-content">
          @((MarkupString)Markdown.ToHtml(MarkdownRenderer.PlanToMarkdown(_plan)))
      </article>
  </SidePanel>
  ```
- The pending-questions banner (amber, with **Answer →** button) **stays** — it's fine inside the chat layout; it's a separate UI affordance from the chat itself.

### 6. Optional polish (defer past this redesign)

- **Snapshot plans per rework** so old plan cards in chat history open the correct historical version. Right now every card opens the current `_plan`. Acceptable MVP; "v1 / v2 / v3" labels would lie if all open the same payload.
- **Charts / graphs** — no markdown renderer covers these. Would need a JS charting library (Chart.js, ApexCharts) wired via JSInterop. Skip until there's a concrete data viz the user wants.
- **Code-block copy buttons** — small JS or a Razor wrapper that adds a "Copy" overlay on hover. Nice-to-have.
- **Streaming agent typing effect** — currently the full agent response appears in one render. Could be a token-by-token stream for that "ChatGPT typing" feel. Not currently easy because Claude one-shot calls return the whole result; would need to switch to stream-json one-shot too.

## Files at the time of pause

```
src/LightsOff.App/Components/Pages/TicketDetail.razor          modified — has standalone plan card to drop
src/LightsOff.App/Components/Shared/ChatThread.razor           modified — needs attachment rendering
src/LightsOff.App/Components/Shared/QuestionWizard.razor       new — works
src/LightsOff.App/Components/Shared/PlanDocument.razor         deleted earlier
src/LightsOff.App/Services/MarkdownRenderer.cs                 unchanged — keep
src/LightsOff.App/wwwroot/app.css                              has .md-content + .chat-fade-in; ADD side-panel keyframe
src/LightsOff.Core/Domain/TicketAnalysis.cs                    has ChatMessage; needs ChatAttachment field
```

## Estimated effort to finish

~3-5 hours of focused work. The pieces are well-isolated and there are no big architectural unknowns left — it's all UI plumbing.

## Resume checklist

- [ ] Add `ChatAttachment` record + extend `ChatMessage` with `Attachment` field
- [ ] Build `PlanCard.razor`
- [ ] Build `SidePanel.razor` + add side-panel CSS keyframe
- [ ] Update `ChatThread.razor` to render attachments + expose `OnAttachmentClick`
- [ ] In `TicketDetail.razor`: drop standalone plan article; inject narrative agent messages at every workflow event; wire `_planPanelOpen`; render `<SidePanel>` with the current plan body
- [ ] Smoke-test: walk through Start → analyze → wizard → plan generated → see chat-only UI with the plan card → click → side panel opens with markdown
- [ ] `dotnet test` still passes (42 tests)
