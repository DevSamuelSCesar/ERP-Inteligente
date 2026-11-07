using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using erp_inteligente.ViewModels;

namespace erp_inteligente.Views;

public partial class NexusView : UserControl
{
    private NexusViewModel? _viewModel;

    public NexusView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.Entries.CollectionChanged -= OnEntriesChanged;

        _viewModel = DataContext as NexusViewModel;
        if (_viewModel is not null)
            _viewModel.Entries.CollectionChanged += OnEntriesChanged;
    }

    /// <summary>Keeps the latest entry in view.</summary>
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Background);

    /// <summary>Enter sends the request; Shift+Enter keeps the default behaviour (new line).</summary>
    private void OnRequestKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || _viewModel is null)
            return;

        e.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null))
            _viewModel.SendCommand.Execute(null);
    }
}
