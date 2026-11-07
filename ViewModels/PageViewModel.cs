using System.Threading.Tasks;

namespace erp_inteligente.ViewModels;

/// <summary>A screen shown in the content container; its title is displayed by the shell.</summary>
public abstract class PageViewModel : ViewModelBase
{
    public abstract string Title { get; }

    /// <summary>Called every time the screen is shown, so data reflects executed plans.</summary>
    public virtual Task OnActivatedAsync() => Task.CompletedTask;
}
