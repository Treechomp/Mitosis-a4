namespace Mitosis.ECS;

/// <summary>
/// The player's remaining continuations for this run.
///
/// Its whole reason for existing is to be SEPARATE from the world. Death and respawn anchor to a
/// faction structure — the nearest Sectid nest, the heart of a Shroomer territory, a Faeling's own
/// crystal — and the obvious shortcut is to let the number of surviving anchors be the number of
/// continuations. That coupling would make run difficulty a function of worldgen: Faeling crystal
/// count is derived from map sense-coverage and recently went from 8 to ~132, which under that
/// shortcut would have silently turned a three-life run into a hundred-life one. A coverage fix
/// must not be a difficulty change.
///
/// So: anchors decide WHERE you come back. This decides HOW MANY TIMES. The fail state is running
/// out of lives, or losing the last structure that could anchor one — two different conditions,
/// and the second is why <see cref="Remaining"/> alone is not the whole answer.
///
/// Nothing consumes this yet; player control, input and the death flow are a later change. It is
/// created with the run so that flow has a home to consume from rather than inventing one.
/// </summary>
public sealed class FactionLives
{
    /// <summary>Continuations the run started with.</summary>
    public int Total { get; }

    /// <summary>Continuations left.</summary>
    public int Remaining { get; private set; }

    /// <summary>Deaths so far (Total - Remaining, kept explicit for logging).</summary>
    public int Spent => Total - Remaining;

    public FactionLives(int total)
    {
        Total = total < 0 ? 0 : total;
        Remaining = Total;
    }

    /// <summary>True while a death can still be answered with a respawn.</summary>
    public bool CanRespawn => Remaining > 0;

    /// <summary>
    /// Spend one continuation. Returns false when there was none to spend — the run is over on
    /// that death, and the caller must not respawn.
    /// </summary>
    public bool Consume()
    {
        if (Remaining <= 0) return false;
        Remaining--;
        return true;
    }
}
