using erp_inteligente.Core.Skills;

namespace Nexus.Tests.Core.Skills;

public class BuiltInSkillsTests
{
    [Fact]
    public void All_ContainsTheSixInitialSkills_AllValid()
    {
        var names = BuiltInSkills.All.Select(s => s.Name).ToList();
        Assert.Equal(["armazenagem", "cadastros", "expedicao", "inventario", "recebimento", "separacao"], names);
        Assert.All(BuiltInSkills.All, s => Assert.Empty(s.Validate()));
    }

    [Fact]
    public void All_RoundTripThroughSerialize()
    {
        foreach (var skill in BuiltInSkills.All)
        {
            var again = SkillDocument.Parse(skill.Serialize()).Document!;
            Assert.Equal(skill.Body, again.Body);
            Assert.Equal(skill.Operations, again.Operations);
        }
    }

    [Fact]
    public void Find_UnknownReturnsNull() => Assert.Null(BuiltInSkills.Find("nao_existe"));
}
