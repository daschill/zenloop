using Xunit;
using ZenLoop.Core;

namespace ZenLoop.Tests;

public class RamTimingTests
{
    [Fact]
    public void RamTimingProfile_round_trips_primary_timings()
    {
        var original = new RamTimingProfile
        {
            MemClockMhz = 3000,
            VddioMv = 1200,
            Tcl = 36,
            Trcd = 36,
            Trp = 36,
            Tras = 76,
            Trfc = 560,
            Expo = false,
        };
        Assert.Equal(6000, original.DataRateMts);
        var parsed = RamTimingProfile.Parse(original.ToJson());
        Assert.Equal(3000, parsed.MemClockMhz);
        Assert.Equal(36, parsed.Tcl);
        Assert.Equal(36, parsed.Trcd);
        Assert.Equal(36, parsed.Trp);
        Assert.Equal(76, parsed.Tras);
        Assert.Equal(560, parsed.Trfc);
        Assert.False(parsed.Expo);
    }

    [Fact]
    public void RamTimingProtocol_apply_args_encode_bios_timings()
    {
        var args = RamTimingProtocol.ApplyArgs(new RamTimingProfile
        {
            MemClockMhz = 3000,
            VddioMv = 1200,
            Tcl = 32,
            Trcd = 38,
            Trp = 38,
            Tras = 70,
            Trfc = 480,
            Expo = false,
        });
        Assert.Equal("ram-apply", args[0]);
        Assert.Equal("3000", args[args.ToList().IndexOf("--clock") + 1]);
        Assert.Equal("32", args[args.ToList().IndexOf("--tcl") + 1]);
        Assert.Equal("38", args[args.ToList().IndexOf("--trcd") + 1]);
        Assert.Equal("70", args[args.ToList().IndexOf("--tras") + 1]);
        Assert.Equal("0", args[args.ToList().IndexOf("--expo") + 1]);
    }

    [Fact]
    public void TryParse_rejects_ok_false_and_does_not_invent_timings()
    {
        Assert.Null(RamTimingProtocol.TryParse(
            """{"ok":false,"error":"CDefaultBIOS not bound","tcl":36,"mem_clock_mhz":3000}"""));
    }

    [Fact]
    public void Apply_then_read_via_fake_helper_round_trips_ram()
    {
        RamTimingProfile? stored = null;
        var backend = new AmdRyzenMasterBackend(args =>
        {
            if (args[0] == "ram-apply")
            {
                Assert.Contains("--tcl", args);
                stored = new RamTimingProfile
                {
                    MemClockMhz = int.Parse(args[args.ToList().IndexOf("--clock") + 1]),
                    Tcl = int.Parse(args[args.ToList().IndexOf("--tcl") + 1]),
                    Trcd = int.Parse(args[args.ToList().IndexOf("--trcd") + 1]),
                    Trp = int.Parse(args[args.ToList().IndexOf("--trp") + 1]),
                    Tras = int.Parse(args[args.ToList().IndexOf("--tras") + 1]),
                    Trfc = int.Parse(args[args.ToList().IndexOf("--trfc") + 1]),
                    VddioMv = int.Parse(args[args.ToList().IndexOf("--vddio") + 1]),
                };
                return "{\"ok\":true,\"bios_persisted\":true,\"ram\":" + stored.ToJson() + "}";
            }
            if (args[0] == "ram-read")
            {
                Assert.NotNull(stored);
                return "{\"ok\":true,\"ram\":" + stored!.ToJson() + "}";
            }
            throw new InvalidOperationException(args[0]);
        });

        var profile = new RamTimingProfile { MemClockMhz = 3000, Tcl = 36, Trcd = 36, Trp = 36, Tras = 76, Trfc = 560, VddioMv = 1200 };
        // Drive shipped apply/read, not a copy of apply.
        var applied = backend.ApplyRam(profile);
        Assert.True(applied.BiosPersisted);
        Assert.Null(applied.Error);

        var read = backend.ReadRam();
        Assert.NotNull(read);
        Assert.Equal(3000, read!.MemClockMhz);
        Assert.Equal(36, read.Tcl);
        Assert.Equal(76, read.Tras);
    }

    [Fact]
    public void Loopback_does_not_fake_ram_bios()
    {
        var smu = new SmuService(new LoopbackSmuBackend());
        var r = smu.ApplyRam(new RamTimingProfile { Tcl = 30 });
        Assert.False(r.BiosPersisted);
        Assert.False(r.Ok);
        Assert.Contains("BIOS", r.Error);
        Assert.DoesNotContain("winring", r.Error, StringComparison.OrdinalIgnoreCase);
    }
}
