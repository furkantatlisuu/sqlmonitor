namespace SqlMonitor.Models;

/// <summary>
/// mon.MonitoredInstance'daki bir satırın ham hâli - "Sunucuları yönet"
/// ekranının okuduğu/yazdığı şekil. Options.InstanceOptions'tan FARKLI:
/// o TEK bir hazır ConnectionString taşır (bağlantı açarken kullanılan
/// şekil), bu ise düzenleme formunun ayrı ayrı ihtiyaç duyduğu alanlara
/// (Server, DatabaseName, UserId, Password ayrı ayrı) bölünmüş hâlidir -
/// InstanceRegistry ikisi arasında çevirir.
/// </summary>
public sealed class InstanceRecord
{
    public string InstanceKey { get; set; } = "";
    public string? DisplayName { get; set; }
    public string Server { get; set; } = "";
    public string DatabaseName { get; set; } = "master";

    /// <summary>RequiresLogin=true ise NULL - appsettings.json'daki Prod kuralıyla aynı disiplin.</summary>
    public string? UserId { get; set; }

    /// <summary>RequiresLogin=true ise NULL. Yönetim ekranına GERİ GÖNDERİLMEZ (bkz. InstanceSummary).</summary>
    public string? Password { get; set; }

    public bool RequiresLogin { get; set; }
    public bool IsDefault { get; set; }
    public bool AutoDiscoverAgReplicas { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>
    /// "host=ip;host=ip" biçiminde, isteğe bağlı. AG replikalarının kayıtlı
    /// adı (sys.availability_replicas.replica_server_name) bazı ağlarda
    /// DNS'ten çözülmüyor - otomatik keşif o zaman replikaya HİÇ ulaşamaz.
    /// Buradaki eşleme varsa SqlConnectionFactory.RebindServer bağlanırken
    /// adın yerine IP'yi kullanır. Boşsa (çoğu kurulumda gerekmez) hiçbir
    /// şey değişmez, ad olduğu gibi kullanılır.
    /// </summary>
    public string? ReplicaHostOverrides { get; set; }

    /// <summary>
    /// Bu instance'ın Critical geçişleri Monitor:SlackWebhookUrl'e gitsin mi.
    /// Varsayılan false - yeni eklenen bir sunucu sessizce bildirim
    /// göndermeye başlamamalı, kullanıcı bilerek açmalı.
    /// </summary>
    public bool SlackAlertsEnabled { get; set; }
}

/// <summary>
/// "Sunucuları yönet" listesinde dönen şekil - Password YOK, yerine
/// yalnızca "bir şifre kayıtlı mı" (HasPassword) var. Ham şifreyi bir kez
/// daha ekrana bastırmak gereksiz risk; düzenlerken kullanıcı yeni bir
/// şifre yazmazsa mevcut olan (varsa) korunur.
/// </summary>
public sealed record InstanceSummary(
    string InstanceKey,
    string? DisplayName,
    string Server,
    string DatabaseName,
    string? UserId,
    bool HasPassword,
    bool RequiresLogin,
    bool IsDefault,
    bool AutoDiscoverAgReplicas,
    string? ReplicaHostOverrides,
    bool SlackAlertsEnabled);
