namespace LightsOff.App.Services;

public enum ToastKind { Info, Success, Warning, Error }

public record Toast(Guid Id, string Message, ToastKind Kind, DateTimeOffset CreatedAt);

public class ToastService
{
    private readonly List<Toast> _toasts = new();
    public IReadOnlyList<Toast> Toasts => _toasts;

    public event Action? Changed;

    public void Show(string message, ToastKind kind = ToastKind.Info, TimeSpan? duration = null)
    {
        var toast = new Toast(Guid.NewGuid(), message, kind, DateTimeOffset.UtcNow);
        lock (_toasts)
        {
            _toasts.Add(toast);
        }
        Changed?.Invoke();

        var lifetime = duration ?? TimeSpan.FromSeconds(4);
        _ = Task.Run(async () =>
        {
            await Task.Delay(lifetime);
            Dismiss(toast.Id);
        });
    }

    public void Success(string message) => Show(message, ToastKind.Success);
    public void Info(string message) => Show(message, ToastKind.Info);
    public void Warning(string message) => Show(message, ToastKind.Warning);
    public void Error(string message) => Show(message, ToastKind.Error);

    public void Dismiss(Guid id)
    {
        lock (_toasts)
        {
            _toasts.RemoveAll(t => t.Id == id);
        }
        Changed?.Invoke();
    }
}
