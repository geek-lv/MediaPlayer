namespace MediaPlayer.Controls.Workflows;

public readonly record struct MediaWorkflowResult(bool Success, string ErrorMessage)
{
    public static MediaWorkflowResult Ok() => new(true, string.Empty);

    public static MediaWorkflowResult Fail(string errorMessage) => new(false, errorMessage);
}
