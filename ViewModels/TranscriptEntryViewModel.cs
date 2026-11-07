using System;

namespace erp_inteligente.ViewModels;

/// <summary>One line of the Nexus transcript: time, author and a body rendered by its own view.</summary>
public sealed class TranscriptEntryViewModel(DateTimeOffset at, string author, ViewModelBase body) : ViewModelBase
{
    public string TimeText { get; } = at.ToLocalTime().ToString("HH:mm");
    public string Author { get; } = author;
    public ViewModelBase Body { get; } = body;
}

/// <summary>Short plain-text answer; never rendered as Markdown.</summary>
public sealed class TextBodyViewModel(string text, bool isError = false) : ViewModelBase
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}
