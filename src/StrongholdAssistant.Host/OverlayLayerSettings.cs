namespace StrongholdAssistant.Host;

#if LINUX_PORT
public sealed record OverlayLayerSettings(bool Placement = false, bool Pending = true, bool Upgrade = true);
#else
public sealed record OverlayLayerSettings(bool Placement = true, bool Pending = true, bool Upgrade = true);
#endif
