namespace Sambient.Dsp.Wavetables;

/// <summary>Registry mapping WAVE index (1..32) to its recipe. Add new recipes to <see cref="All"/>.</summary>
public static class WaveRecipes
{
    private static readonly IWaveRecipe[] All =
    [
        new Recipes.SawS(),
        new Recipes.SqrD(),
        new Recipes.SinS(),
        new Recipes.SinD(),
        new Recipes.SqrS(),
        new Recipes.TriS(),
        new Recipes.SqSn(),
        new Recipes.EvOd(),
        new Recipes.Octv(),
        new Recipes.SinM(),
        new Recipes.SinF(),
        new Recipes.TriF(),
        new Recipes.SinC(),
        new Recipes.FmCr(),
        new Recipes.FmOd(),
        new Recipes.Sync(),
        new Recipes.Hrm1(),
        new Recipes.Hrm2(),
        new Recipes.ClFm(),
        new Recipes.Frml(),
        new Recipes.Frm1(),
        new Recipes.Frm2(),
        new Recipes.Ps27(),
        new Recipes.FmWf(),
        new Recipes.FmPf(),
        new Recipes.AdMp(),
        new Recipes.Bchl(),
        new Recipes.SqrR(),
        new Recipes.Rndm(),
        new Recipes.SRnd(),
        new Recipes.TriN(),
        new Recipes.WhNs(),
    ];

    private static readonly IWaveRecipe?[] ByIndex = BuildIndex();

    private static IWaveRecipe?[] BuildIndex()
    {
        var a = new IWaveRecipe?[WavetableBank.MaxWaves + 1];
        foreach (var r in All) a[r.Index] = r;
        return a;
    }

    /// <summary>All implemented recipes, ordered by index.</summary>
    public static IReadOnlyList<IWaveRecipe> Implemented { get; } = All.OrderBy(r => r.Index).ToArray();

    public static bool IsImplemented(int index) => (uint)index <= WavetableBank.MaxWaves && ByIndex[index] != null;

    public static bool TryGet(int index, out IWaveRecipe recipe)
    {
        var r = (uint)index <= WavetableBank.MaxWaves ? ByIndex[index] : null;
        recipe = r!;
        return r != null;
    }
}
