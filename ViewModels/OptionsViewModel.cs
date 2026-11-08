using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>
/// Next steps suggested by the Nexus (references/UI.md, section 4.3), shown as neutral buttons under the answer.
/// Choosing one sends its request as if the operator had typed it; the row then stays read-only.
/// </summary>
public sealed partial class OptionsViewModel : ViewModelBase
{
    public const int MaxOptions = 4;
    public const int MaxLabelLength = 60;
    public const int MaxRequestLength = 300;

    private readonly Func<string, Task> _send;
    private readonly Func<bool> _canSend;

    public OptionsViewModel(IEnumerable<NexusOption> options, Func<string, Task> send, Func<bool> canSend)
    {
        _send = send;
        _canSend = canSend;
        Options = options
            .Where(o => !string.IsNullOrWhiteSpace(o.Label) && !string.IsNullOrWhiteSpace(o.Request))
            .Take(MaxOptions)
            .Select(o => new NexusOption(Clip(o.Label, MaxLabelLength), Clip(o.Request, MaxRequestLength)))
            .ToList();
    }

    public IReadOnlyList<NexusOption> Options { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    public partial NexusOption? Chosen { get; private set; }

    private bool CanChoose(NexusOption? option) => option is not null && Chosen is null && _canSend();

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task ChooseAsync(NexusOption? option)
    {
        Chosen = option;
        await _send(option!.Request);
    }

    /// <summary>Re-evaluates the buttons when the chat becomes busy or free.</summary>
    public void Refresh() => ChooseCommand.NotifyCanExecuteChanged();

    private static string Clip(string text, int max)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
