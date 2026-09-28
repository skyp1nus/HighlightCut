namespace HighlightCut.Core.Editing;

/// <summary>An edit that cannot be applied, e.g. an unknown clip or an empty range.</summary>
public sealed class EditException : Exception
{
    public EditException()
    {
    }

    public EditException(string message)
        : base(message)
    {
    }

    public EditException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
