version "4.0"

// A static handler is deliberately not serialized into the user's save game.
// A transition alone is NOT completion. Require the engine's LEVEL_VISITED bit
// to change from clear to set after DoCompleted. Console changemap skips that
// bit; map/load/restart have no transition target. Hub travel is not completion.
class DL667MapTrackerV1 : StaticEventHandler
{
    LevelInfo PendingInfo;
    String PendingMap;
    bool WasVisited;
    int Serial;
    ui int PrintedSerial;

    bool Validated() const
    {
        return PendingInfo != null && PendingMap != ""
            && (PendingInfo.flags & LEVEL_VISITED) != 0;
    }

    void Report() const
    {
        let session = CVar.FindCVar('dl667_track_session');
        if (session != null)
            Console.Printf("DL667_TRACK_V1|%s|complete|%s", session.GetString(), PendingMap);
    }

    override void WorldLoaded(WorldEvent e)
    {
        if (!e.IsSaveGame && Validated()) Report();
        PendingInfo = null;
        PendingMap = "";
        WasVisited = level.info != null && (level.info.flags & LEVEL_VISITED) != 0;
        let session = CVar.FindCVar('dl667_track_session');
        if (session != null)
            Console.Printf("DL667_TRACK_V1|%s|ready", session.GetString());
    }

    override void WorldUnloaded(WorldEvent e)
    {
        PendingInfo = null;
        PendingMap = "";
        if (e.IsSaveGame || multiplayer || demoplayback || WasVisited
            || e.NextMap == "" || level.info == null
            || (level.clusterflags & LevelLocals.CLUSTER_HUB) != 0) return;
        PendingInfo = level.info;
        PendingMap = level.MapName;
        Serial++;
    }

    override void UiTick()
    {
        // Intermissions/finales need no subsequent WorldLoaded, including quit
        // at an intermission. UI code only reads the play-scope evidence.
        if (PrintedSerial != Serial && Validated()
            && (gamestate == GS_INTERMISSION || gamestate == GS_FINALE || gamestate == GS_CUTSCENE))
        {
            Report();
            PrintedSerial = Serial;
        }
    }
}
