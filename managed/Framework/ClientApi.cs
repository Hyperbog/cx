// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Framework/ClientApi.cs
// PURPOSE: Engine API bridge
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Native pointers and engine calls remain build-locked and guarded. Never
//   assume Windows offsets are stable across NWN:EE updates.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using CX.Engine;

namespace CX.Framework;

/// <summary>
/// Small shared bridge to the client's message object, so services don't each re-resolve
/// the send plumbing. Lives in the framework layer: cross-cutting plumbing every feature calls,
/// not a feature of its own.
/// </summary>
public static unsafe class ClientApi
{
    private static readonly IntPtr AppMgr = Native.Sym(SymbolMap.AppManager);
    /// <summary>
    /// A live CNWCMessage* captured from a hook that is handed one (inbound `self` / login `this`).
    /// Preferred over resolving GetNWCMessage(*g_pAppManager) -- exactly the engine's own object, no
    /// symbol/offset guesswork. The g_pAppManager path (which segfaulted on this build) is fallback only.
    /// </summary>
    public static IntPtr LiveMessage;

    /// <summary>A live CClientExoApp* captured from the focus hooks (their `this`). A process-lifetime
    /// singleton, so one capture holds; lets services walk to the live in-game GUI without the flaky
    /// g_pAppManager global.</summary>
    public static IntPtr LiveExoApp;

    public static IntPtr Message()
    {
        // Never dereference AppMgr here. On NWN:EE 8193.37-17 the fallback
        // GetNWCMessage() can read 0x208 before the engine message object is
        // available. A hook-published message is safe; otherwise send nothing.
        return LiveMessage;
    }

    public static void Send(byte major, byte minor, byte* payload, uint len)
    {
        IntPtr msg = Message();
        if (msg != IntPtr.Zero && EngineFn.SendPlayerToServerMessage != null) EngineFn.SendPlayerToServerMessage(msg, major, minor, payload, len);
    }

    /// <summary>
    /// Send an ASCII payload. Footgun: SendPlayerToServerMessage reserves the first 3 bytes for its
    /// own ['p'][major][minor] header and transmits the whole len, so we reserve 3 bytes up front and
    /// put the ASCII at offset 3 -- handing it the raw payload sends a minor-less runt the server drops.
    /// </summary>
    public static void SendAscii(byte major, byte minor, string payload)
    {
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(payload);
        byte[] buf = new byte[3 + ascii.Length];
        System.Array.Copy(ascii, 0, buf, 3, ascii.Length);
        fixed (byte* p = buf) Send(major, minor, p, (uint)buf.Length);
    }
}
