using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>
/// A discreet line under the answer when the Nexus saved a memory (references/UI.md, section 4.4). Informative only:
/// the memory is already saved through the protection filter and can be archived in the Memórias screen.
/// </summary>
public sealed class MemoryNoteViewModel(NexusMemoryNote note) : ViewModelBase
{
    public string Text { get; } = (note.Reinforced ? "Memória reforçada: " : "Memória registrada: ") + note.Content;
}
