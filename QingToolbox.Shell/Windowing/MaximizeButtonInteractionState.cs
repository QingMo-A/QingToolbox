namespace QingToolbox.Shell.Windowing;

public sealed class MaximizeButtonInteractionState
{
    public bool IsPressed { get; private set; }
    public void Press() => IsPressed = true;
    public bool Release(bool isPointerOverButton)
    {
        var invoke = IsPressed && isPointerOverButton;
        IsPressed = false;
        return invoke;
    }
    public void Cancel() => IsPressed = false;
}
