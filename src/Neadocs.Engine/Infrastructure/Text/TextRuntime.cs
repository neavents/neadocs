namespace Neadocs.Engine.Infrastructure.Text;

using System.Text;

public static class TextRuntime
{
    private const string Precomposed = "é";
    private const string Decomposed = "é";

    public static bool SupportsNormalization { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            return Precomposed.Normalize(NormalizationForm.FormD).Length == 2
                && Decomposed.Normalize(NormalizationForm.FormC).Length == 1;
        }
        catch (System.Exception)
        {
            return false;
        }
    }
}
