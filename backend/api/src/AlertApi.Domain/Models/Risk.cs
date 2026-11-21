namespace AlertApi.Domain.Models;

// How much an action can change. The highest risk of a run's steps decides how the run starts (#73).
public enum Risk
{
    ReadOnly = 0,
    Auto = 1,
    Approval = 2
}

public static class RiskNames
{
    public const string ReadOnly = "read_only";
    public const string Auto = "auto";
    public const string Approval = "approval";

    public static readonly string[] All = [ReadOnly, Auto, Approval];

    public static string ToName(this Risk risk)
    {
        return risk switch
        {
            Risk.ReadOnly => ReadOnly,
            Risk.Auto => Auto,
            _ => Approval
        };
    }

    public static Risk? Parse(string? name)
    {
        return name switch
        {
            ReadOnly => Risk.ReadOnly,
            Auto => Risk.Auto,
            Approval => Risk.Approval,
            _ => null
        };
    }

    public static Risk Max(Risk first, Risk second)
    {
        return first >= second ? first : second;
    }
}
