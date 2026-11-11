namespace GeoAutoDocs.Core;

public enum MediaLoadIntent
{
    ContextSelection = 0,
    ExplicitVideoSelection = 1,
    Reload = 2,
    NavigateToEvidence = 3,
    NewlyAddedVideo = 4,
    RelocatedVideo = 5
}

public static class MediaLoadPolicy
{
    public static bool CanOpen(MediaLoadIntent intent) => intent is not MediaLoadIntent.ContextSelection;
}
