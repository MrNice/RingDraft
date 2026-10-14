using System.Collections.Concurrent;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// The one message type this mod adds to the game's own co-op channel. The payload is a
/// base64 <see cref="Codec"/> envelope, so all real parsing lives in the tested core.
/// Shape copied from a working public co-op mod (sts2_typing's ChatMessage).
/// </summary>
public struct RingDraftMessage : INetMessage, IPacketSerializable
{
    public string payload;

    public bool ShouldBroadcast => true;
    public bool ShouldBuffer => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;

    public void Serialize(PacketWriter writer) => writer.WriteString(payload ?? string.Empty);
    public void Deserialize(PacketReader reader) => payload = reader.ReadString();
}

/// <summary>Attaches to the run's net service when a multiplayer run is live, and detaches when it isn't.</summary>
internal sealed class RingNet
{
    private INetGameService? _net;
    private readonly ConcurrentQueue<(Envelope Env, ulong Sender)> _incoming = new();

    public bool Active => _net is not null;
    public ulong LocalId => _net?.NetId ?? 0;
    public bool IsHost => _net?.Type == NetGameType.Host;

    public void Poll()
    {
        var available = Current();
        if (available is not null && available != _net)
        {
            Detach();
            _net = available;
            _net.RegisterMessageHandler<RingDraftMessage>(OnMessage);
            Log.Info($"Attached to multiplayer run as {_net.NetId}");
        }
        else if (available is null && _net is not null)
        {
            Detach();
        }
    }

    public void Detach()
    {
        if (_net is null) return;
        try { _net.UnregisterMessageHandler<RingDraftMessage>(OnMessage); } catch { }
        _net = null;
        while (_incoming.TryDequeue(out _)) { }
        Log.Info("Detached from multiplayer run");
    }

    public bool TryReceive(out Envelope env, out ulong sender)
    {
        if (_incoming.TryDequeue(out var item)) { env = item.Env; sender = item.Sender; return true; }
        env = null!; sender = 0;
        return false;
    }

    public void Send(Envelope e)
    {
        if (_net is null) return;
        try { _net.SendMessage(new RingDraftMessage { payload = Codec.ToBase64(e) }); }
        catch (Exception ex) { Log.Error($"Send {e.GetType().Name} failed", ex); }
    }

    public string NameOf(ulong id)
    {
        if (_net is null) return id.ToString();
        try { return PlatformUtil.GetPlayerName(_net.Platform, id); }
        catch { return id.ToString(); }
    }

    // May arrive off the main thread; queue it and handle it in _Process.
    private void OnMessage(RingDraftMessage msg, ulong senderId)
    {
        if (Codec.TryFromBase64(msg.payload, out var env) && env is not null)
            _incoming.Enqueue((env, senderId));
        else
            Log.Warn($"Dropped unreadable message from {senderId} (version mismatch?)");
    }

    private static INetGameService? Current()
    {
        var rm = RunManager.Instance;
        if (rm is not null && rm.IsInProgress && rm.NetService?.Type.IsMultiplayer() == true && rm.NetService.IsConnected)
            return rm.NetService;
        return null;
    }
}
