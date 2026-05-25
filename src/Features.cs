namespace WinLens;

/// <summary>
/// Compile-time feature flags. Experimental features are enabled only in Debug builds so
/// they can be tested locally without shipping in a Release. Flip to a const true (or remove
/// the guard) when a feature is ready to release.
/// </summary>
public static class Features
{
    // Recent target languages pinned atop the picker. Shipped on after testing in Debug.
    public const bool RecentLanguages = true;
}
