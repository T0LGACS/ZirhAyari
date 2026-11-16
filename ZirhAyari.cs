using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.UserMessages;

namespace ZirhAyari;

// Bedava zırh ayarını haritaya karşı korur (kullanıcı 2026-10-24: "aim_redline yapımcısı kafa armorunu forcelamış").
// Bazı Workshop haritaları kendi içinden "mp_free_armor 2" çalıştırıp sunucunun ayarını ezer (aim_redline böyle).
// Bu eklenti: her harita ve raunt başında ayarı tekrar yazar (haritanın gecikmeli komutundan sonra da), ayrıca doğuşta
// ayara aykırı kask/zırhı alır. Önceki raunttan sağ çıkıp kaskını koruyanlara dokunmaz (normal oyun kuralı).
public class ZirhConfig : BasePluginConfig
{
    // 0 = zırh yok, 1 = sadece yelek, 2 = yelek + kask (CS2 mp_free_armor ile aynı)
    [JsonPropertyName("FreeArmor")] public int FreeArmor { get; set; } = 1;
    // Sadece bu haritalarda çalışır (ad içinde geçmesi yeter). Kullanıcı 2026-10-24: "sadece redline için geçerli olsun,
    // diğer haritalarda istemiyorum" -> diğer haritalarda eklenti hiçbir şeye dokunmaz, harita/sunucu ayarı geçerli.
    [JsonPropertyName("Maps")] public List<string> Maps { get; set; } = new() { "aim_redline" };
    // Para ödülü yazıları sadece adı bunlarla başlayan haritalarda gizlenir (kullanıcı 2026-11-06: "tüm aim_ awp_ maplerine")
    [JsonPropertyName("HideCashMaps")] public List<string> HideCashMaps { get; set; } = new() { "aim_", "awp_" };
}

public class ZirhAyari : BasePlugin, IPluginConfig<ZirhConfig>
{
    public override string ModuleName => "ZirhAyari";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "Claude";
    public override string ModuleDescription => "mp_free_armor ayarını haritanın ezmesine karşı korur; aim_/awp_ haritalarında para ödülü yazılarını gizler.";

    public ZirhConfig Config { get; set; } = new();
    public void OnConfigParsed(ZirhConfig config) => Config = config;

    private readonly HashSet<ulong> _kept = new(); // önceki raunt sonunda canlı ve kasklı olanlar

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ => { _kept.Clear(); ApplyLater(); });
        ApplyLater();
        // Para ödülü yazıları ("+$300: düşman öldürme", raunt sonu takım parası) gizlenir, para yine verilir
        // (kullanıcı 2026-11-06). Sadece HideCashMaps haritalarında. CS2'de TextMsg = 124.
        // FindIdByName("CUserMessageTextMsg") bu CSS sürümünde hata fırlatıp eklentiyi tamamen düşürdü (2026-11-07), id elle verilir.
        // Hook kurulamazsa zırh ayarı yine çalışsın.
        try
        {
            HookUserMessage(124, um =>
            {
                if (!HideCash()) return HookResult.Continue;
                var text = um.GetRepeatedFieldCount("param") > 0 ? um.ReadString("param", 0) : "";
                return text.StartsWith("#Player_Cash_Award") || text.StartsWith("#Team_Cash_Award") ? HookResult.Handled : HookResult.Continue;
            });
            Console.WriteLine("[ZirhAyari] para yazısı gizleme: TextMsg (124) dinleniyor");
        }
        catch (Exception ex) { Console.WriteLine($"[ZirhAyari] para yazısı gizleme kurulamadı: {ex.Message}"); }
    }

    // Sunucu açılırken eklentiler haritadan önce yüklenir; harita adı henüz yokken çöküp hiç çalışmıyordu (2026-10-24)
    private bool Active()
    {
        string map;
        try { map = Server.MapName ?? ""; } catch { return false; }
        return map != "" && Config.Maps.Any(m => map.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private bool HideCash()
    {
        string map;
        try { map = Server.MapName ?? ""; } catch { return false; }
        return Config.HideCashMaps.Any(m => map.StartsWith(m, StringComparison.OrdinalIgnoreCase));
    }

    private void Apply() { if (Active()) Server.ExecuteCommand("mp_free_armor " + Math.Clamp(Config.FreeArmor, 0, 2)); }

    // Haritanın komutu gecikmeli olabilir: hemen ve birkaç saniye sonra tekrar yaz
    private void ApplyLater()
    {
        Apply();
        foreach (var s in new[] { 0.5f, 2f, 5f }) AddTimer(s, Apply);
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart e, GameEventInfo info) { ApplyLater(); return HookResult.Continue; }

    [GameEventHandler]
    public HookResult OnNewMatch(EventBeginNewMatch e, GameEventInfo info) { _kept.Clear(); ApplyLater(); return HookResult.Continue; }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd e, GameEventInfo info)
    {
        _kept.Clear();
        foreach (var p in Utilities.GetPlayers())
        {
            var pawn = p.IsValid ? p.PlayerPawn.Value : null;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.ItemServices == null) continue;
            if (new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasHelmet) _kept.Add(p.SteamID);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnSpawn(EventPlayerSpawn e, GameEventInfo info)
    {
        var player = e.Userid;
        if (player == null || !player.IsValid || Config.FreeArmor >= 2 || !Active()) return HookResult.Continue;
        var sid = player.SteamID;
        AddTimer(0.2f, () =>
        {
            if (!player.IsValid || _kept.Contains(sid)) return;
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.ItemServices == null) return;
            var items = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
            if (items.HasHelmet)
            {
                items.HasHelmet = false;
                Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pItemServices");
            }
            if (Config.FreeArmor == 0 && pawn.ArmorValue > 0)
            {
                pawn.ArmorValue = 0;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
            }
        });
        return HookResult.Continue;
    }
}
