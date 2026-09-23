/*
    SQL izleme — istemci

    Framework yok, derleme adımı yok. Bunun sebebi kolaycılık değil:
    bir izleme aracının kurulumu mümkün olduğunca az parçadan oluşmalı.
    npm bağımlılığı olmayan bir arayüz, üç yıl sonra da açılır.

    Akış: sayfa açılınca instance listesi çekilir, sonra seçilen aralıkta
    /api/live/snapshot çağrılır ve DOM yeniden çizilir.
*/

'use strict';

const state = {
    instance: null,

    // Gerçek başlangıç değeri start()'ta #intervalSelect'in kendi HTML
    // "selected" seçeneğinden okunuyor (bkz. aşağısı) - burada yalnızca
    // bir yer tutucu. Önceden burada SABİT bir sayı (1) vardı ve
    // #intervalSelect'in varsayılanından BAĞIMSIZ çalışıyordu - dropdown
    // "5 saniye" gösterse bile uygulama gerçekte 1 saniyede bir
    // sorguluyordu, kullanıcı elle değiştirene kadar. Artık TEK doğruluk
    // kaynağı HTML'deki "selected" - burada tekrar bir sayı yazmak, ikisi
    // birbirinden sapabileceği için BİLEREK yapılmadı.
    intervalSeconds: null,
    timer: null,
    inFlight: false,
    lastTimelineAt: 0,
    lastSnapshot: null,

    // Olay kanıtı: eventId -> kanıt satırları. Geçmiş bir olayın kanıtı
    // değişmediği için bir kez çekilip tutuluyor (bkz. showEventDetail).
    eventEvidence: {},

    // Index Analizi bilerek 1 saniyelik döngünün dışında - eksik index önerileri dakikalar
    // içinde önemli ölçüde değişmez, saniyelik tazelik gerekmiyor.
    miLoaded: false,
    miInFlight: false,

    // Stored Procedure de aynı disiplinle - plan cache'e ihtiyaç var.
    spSort: 'cpu',
    spDir: 'desc',
    spLoaded: false,
    spInFlight: false,

    // Kullanılmayan Prosedürler - çok-veritabanlı tarama, ayrı yükleniyor.
    // unusedData ham sonucu tutuyor ki "yalnızca güçlü kanıt" anahtarı
    // sunucuya tekrar sormadan, istemci tarafında filtreleyip yeniden çizebilsin.
    unusedLoaded: false,
    unusedInFlight: false,
    unusedData: null,

    // Always On - aynı disiplin, sekme açıldığında yüklenir.
    agLoaded: false,
    agInFlight: false,

    // Agent Jobs - aynı disiplin.
    jobsLoaded: false,
    jobsInFlight: false,

    // Bekleme trendi (Genel Bakış'taki Bekleme kartının alt bölümü) -
    // snapshot döngüsünün dışında, bkz. loadWaitTrend.
    waitTrendLoaded: false,
    waitTrendInFlight: false,

    // RequiresLogin=true olan instance'lar (Prod) için: /api/instances'tan
    // gelen { name -> {name, displayName, isDefault, requiresLogin} }
    // eşlemesi - her seferinde ayrı bir istek atmadan "bu instance kilitli
    // mi" sorusuna cevap vermek için.
    instanceMeta: {},

    // Giriş kapısını KAPAT (×) tıklandığında dönülecek son "kilitsiz"
    // instance - kullanıcı Prod'u seçip şifre girmeden vazgeçtiğinde
    // sayfayı yenilemek zorunda kalmasın diye (bkz. handleAuthCancel).
    lastGoodInstance: null,

    // "Sunucuları yönet" ekranı - bkz. loadInstanceManagerList/openInstanceForm.
    // imEditingKey: null = yeni sunucu formu, string = o anahtarı düzenliyoruz.
    instMgrItems: [],
    imEditingKey: null
};

const $ = (id) => document.getElementById(id);

// ------------------------------------------------------------------
// Biçimlendirme
// ------------------------------------------------------------------

const nf0 = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 });
const nf1 = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 1 });
const nf2 = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 2 });

/** Değer yoksa "—" göster. Uydurma sıfır göstermek yalan söylemektir. */
function num(value, decimals = 0) {
    if (value === null || value === undefined) return '—';
    const fmt = decimals === 0 ? nf0 : (decimals === 1 ? nf1 : nf2);
    return fmt.format(value);
}

function sevClass(severity) {
    return 'sev-' + String(severity || 'unknown').toLowerCase();
}

function sevLabel(severity) {
    switch (String(severity).toLowerCase()) {
        case 'healthy':  return 'sağlıklı';
        case 'warning':  return 'uyarı';
        case 'critical': return 'kritik';
        default:         return 'bilinmiyor';
    }
}

function duration(ms) {
    if (ms === null || ms === undefined) return '—';
    const s = Math.floor(ms / 1000);
    if (s < 60) return s + ' sn';
    const m = Math.floor(s / 60);
    if (m < 60) return m + ' dk ' + (s % 60) + ' sn';
    return Math.floor(m / 60) + ' sa ' + (m % 60) + ' dk';
}

/**
 * Mantıksal okuma/yazma sayıları SQL Server'da her zaman 8 KB'lık SAYFA
 * birimindedir (sys.dm_exec_procedure_stats belgeli, sabit bir mimari
 * sabiti - sürüme göre değişmez). "1.133.371.065 sayfa" kimseye bir şey
 * anlatmaz; "8,44 TB" anlatır. duration()'ın ms→sn→dk→sa yaptığı otomatik
 * birim seçimini burada bayt için yapıyoruz.
 */
const PAGE_SIZE_BYTES = 8192;

function pagesToSize(pages) {
    if (pages === null || pages === undefined) return '—';
    let bytes = pages * PAGE_SIZE_BYTES;
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    while (bytes >= 1024 && i < units.length - 1) { bytes /= 1024; i++; }
    return num(bytes, bytes < 10 ? 2 : (bytes < 100 ? 1 : 0)) + ' ' + units[i];
}

/** Çağrı başına ortalama için SABİT MB - toplamın aksine otomatik birim
    değiştirmiyor (tek bir çağrının okuması neredeyse her zaman KB-MB
    aralığındadır, sabit birim satırlar arası kıyaslamayı kolaylaştırır). */
function pagesToMB(pages) {
    if (pages === null || pages === undefined) return '—';
    return num((pages * PAGE_SIZE_BYTES) / (1024 * 1024), 2) + ' MB';
}

function clockTime(isoUtc) {
    const d = new Date(isoUtc.endsWith('Z') ? isoUtc : isoUtc + 'Z');
    return d.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' });
}

function dayTime(isoUtc) {
    const d = new Date(isoUtc.endsWith('Z') ? isoUtc : isoUtc + 'Z');
    return d.toLocaleString('tr-TR', {
        day: '2-digit', month: '2-digit',
        hour: '2-digit', minute: '2-digit'
    });
}

/** XSS'e karşı: sunucudan gelen her metin buradan geçer. */
function esc(text) {
    const div = document.createElement('div');
    div.textContent = text === null || text === undefined ? '' : String(text);
    return div.innerHTML;
}

/*
    SQL söz dizimi renklendirme.

    Tam bir ayrıştırıcı değil - regex tabanlı, tek geçişli bir isaretleyici.
    Amaç mükemmel bir SQL parser'ı değil, koyu kod kutusunun tekdüze griden
    çıkıp okumayı kolaylaştıran bir görsel katman kazanması.

    TEK GEÇİŞ şart: sırayla birden çok replace() çağırsaydık (önce
    anahtar kelime, sonra string, ...) bir replace'in ürettiği <span>
    etiketi bir SONRAKİ replace'e ham metin gibi görünür ve kendi içine
    tekrar renklendirme sokuşturabilirdi - klasik "highlight-inside-
    highlight" hatası. Tek regex + exec döngüsü bunu yapısal olarak
    imkansız kılıyor: her karakter en fazla bir kez eşleşir.

    Eşleşmeyen her parça da esc()'ten geçiyor - highlightSql çıktısı,
    highlight'sız halinin aynı güvenlik garantisini taşır.
*/
const SQL_TOKEN_RE = new RegExp(
    '(--[^\\n]*)' +                                  // 1: satır yorumu
    '|(/\\*[\\s\\S]*?\\*/)' +                         // 2: blok yorumu
    "|('(?:[^']|'')*')" +                             // 3: string literal
    '|(@\\w+)' +                                      // 4: @değişken
    '|(#{1,2}[A-Za-z_]\\w*)' +                         // 5: #geçici / ##global tablo
    '|(\\b\\d+\\.?\\d*\\b)' +                          // 6: sayı
    '|(\\b(?:' + [
        'SELECT','FROM','WHERE','JOIN','INNER','LEFT','RIGHT','FULL','OUTER','ON',
        'AND','OR','NOT','NULL','IS','IN','EXISTS','GROUP','ORDER','BY','HAVING',
        'INSERT','INTO','VALUES','UPDATE','SET','DELETE','MERGE','USING','MATCHED',
        'CREATE','ALTER','DROP','PROCEDURE','FUNCTION','TABLE','VIEW','INDEX',
        'DECLARE','BEGIN','END','AS','WITH','NOLOCK','TOP','DISTINCT','UNION','ALL',
        'CASE','WHEN','THEN','ELSE','COUNT','SUM','MAX','MIN','AVG','CONVERT','CAST',
        'ISNULL','COALESCE','OPTION','RECOMPILE','IF','WHILE','RETURN','EXEC','EXECUTE',
        'OUTPUT','DEFAULT','PRIMARY','KEY','REFERENCES','CROSS','APPLY','OVER',
        'PARTITION','ASC','DESC','BETWEEN','LIKE','TRY','CATCH','THROW','TRAN',
        'TRANSACTION','COMMIT','ROLLBACK','VARCHAR','NVARCHAR','INT','BIGINT',
        'DECIMAL','DATETIME','BIT'
    ].join('|') + ')\\b)',                             // 7: anahtar kelime
    'gi');

function highlightSql(text) {
    if (!text) return '';
    SQL_TOKEN_RE.lastIndex = 0;

    let out = '';
    let last = 0;
    let m;
    while ((m = SQL_TOKEN_RE.exec(text))) {
        out += esc(text.slice(last, m.index));
        const cls = m[1] || m[2] ? 'sql-comment'
                  : m[3] ? 'sql-string'
                  : m[4] ? 'sql-var'
                  : m[5] ? 'sql-temp'
                  : m[6] ? 'sql-number'
                  : 'sql-kw';
        out += `<span class="${cls}">${esc(m[0])}</span>`;
        last = SQL_TOKEN_RE.lastIndex;
    }
    out += esc(text.slice(last));
    return out;
}

// ------------------------------------------------------------------
// Veri çekme
// ------------------------------------------------------------------

async function getJson(url) {
    const res = await fetch(url, { headers: { 'Accept': 'application/json' } });

    if (res.status === 401) {
        // İki farklı 401 nedeni ayrı ele alınmalı: "login_required" bu
        // instance'ın kilitli olduğunu söyler (giriş formunu göster),
        // başka bir 401 gerçek bir hatadır (olduğu gibi fırlat).
        let body = null;
        try { body = await res.json(); } catch { /* gövde JSON değilse önemi yok */ }

        if (body && body.error === 'login_required') {
            showAuthGate();
            throw new Error('Bu sunucu için giriş gerekli.');
        }
        throw new Error(url + ' → HTTP 401');
    }

    if (!res.ok) throw new Error(url + ' → HTTP ' + res.status);
    return res.json();
}

/**
 * Sunucu seçicisini ve state.instanceMeta'yı /api/instances'tan tazeler.
 *
 * İlk açılışta (loadInstances) ve "Sunucuları yönet" ekranında bir
 * ekleme/düzenleme/silme sonrası (afterInstanceListChanged) İKİSİNDE de
 * kullanılıyor - ama DAVRANIŞLARI kasıtlı olarak farklı: şu an seçili
 * instance yeni listede HÂLÂ varsa seçimi KORUR (bir düzenlemeden sonra
 * kullanıcıyı sessizce varsayılana fırlatmak şaşırtıcı olurdu); yalnızca
 * seçili olan silinmişse ya da hiç seçim yoksa (ilk açılış) varsayılana
 * düşer.
 */
async function refreshInstanceOptions() {
    const list = await getJson('/api/instances');
    const select = $('instanceSelect');
    const previous = state.instance;

    select.innerHTML = list
        .map(i => `<option value="${esc(i.name)}">${esc(i.displayName)}</option>`)
        .join('');

    state.instanceMeta = {};
    list.forEach(i => { state.instanceMeta[i.name] = i; });

    if (previous && list.some(i => i.name === previous)) {
        select.value = previous;
    } else {
        const preferred = list.find(i => i.isDefault) || list[0];
        state.instance = preferred ? preferred.name : null;
        if (preferred) select.value = preferred.name;
    }

    return list;
}

async function loadInstances() {
    await refreshInstanceOptions();
}

// ------------------------------------------------------------------
// Giriş kapısı (RequiresLogin=true instance'lar - Prod)
//
// appsettings.json bu instance için hiçbir kimlik bilgisi TAŞIMIYOR -
// girişi SQL Server'ın kendisi doğruluyor (Api/AuthEndpoints.cs gerçek
// bir bağlantı dener). Kilit SÜREÇ başınadır (bkz. ProdCredentialStore
// üstündeki not) - bu sekmenin kapanıp açılması ya da başka bir sekmede
// zaten girilmiş olması kilidi etkiler, tarayıcıya özel bir durum değildir.
// ------------------------------------------------------------------

function currentInstanceRequiresLogin() {
    const meta = state.instanceMeta[state.instance];
    return !!(meta && meta.requiresLogin);
}

function updateLogoutButton(unlocked) {
    const btn = $('authLogout');
    if (!btn) return;
    btn.hidden = !(currentInstanceRequiresLogin() && unlocked);
}

/** Seçili instance kilitliyse giriş formunu gösterir ve false döner; değilse (ya da zaten açıksa) true döner. */
async function ensureAuthorized() {
    if (!currentInstanceRequiresLogin()) {
        state.lastGoodInstance = state.instance;
        hideAuthGate();
        updateLogoutButton(false);
        return true;
    }

    try {
        const status = await getJson('/api/auth/status?instance=' + encodeURIComponent(state.instance));
        if (status.unlocked) {
            state.lastGoodInstance = state.instance;
            hideAuthGate();
            updateLogoutButton(true);
            return true;
        }
    } catch {
        // Durum bile alınamadıysa temkinli davran: giriş formunu göster.
    }

    showAuthGate();
    return false;
}

function showAuthGate() {
    if (state.timer) clearInterval(state.timer);

    const meta = state.instanceMeta[state.instance];
    $('authInstanceName').textContent = (meta && meta.displayName) || state.instance;

    // BUG (bulundu/düzeltildi): alanlar önceden hiç temizlenmiyordu -
    // yanlış bir şifre denendikten sonra ya da FARKLI kilitli bir
    // instance'a geçildiğinde (iki instance aynı #authUsername/#authPassword
    // alanlarını PAYLAŞIYOR) önceki denemenin kullanıcı adı/şifresi
    // ekranda asılı kalıyordu - hem kafa karıştırıcı hem de bu instance'a
    // ait olmayan bir bilgiyi göstermek yanlış.
    $('authUsername').value = '';
    $('authPassword').value = '';
    $('authError').hidden = true;
    $('authGate').hidden = false;
    updateLogoutButton(false);
    $('authUsername').focus();
}

function hideAuthGate() {
    $('authGate').hidden = true;
}

async function handleAuthSubmit(e) {
    e.preventDefault();

    const username = $('authUsername').value.trim();
    const password = $('authPassword').value;
    const btn = $('authSubmit');
    const err = $('authError');

    err.hidden = true;
    btn.disabled = true;
    btn.textContent = 'Kontrol ediliyor…';

    try {
        const res = await fetch('/api/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ instance: state.instance, username, password })
        });
        const data = await res.json().catch(() => ({}));

        if (!res.ok) {
            err.textContent = data.error || ('HTTP ' + res.status);
            err.hidden = false;
            return;
        }

        $('authPassword').value = '';
        hideAuthGate();
        updateLogoutButton(true);
        await refresh();
        reloadActiveTab();
        restartTimer();
    } catch (ex) {
        err.textContent = 'Bağlanılamadı: ' + ex.message;
        err.hidden = false;
    } finally {
        btn.disabled = false;
        btn.textContent = 'Giriş yap';
    }
}

async function handleAuthLogout() {
    const instance = state.instance;
    try {
        await fetch('/api/auth/logout', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ instance })
        });
    } catch {
        // Çıkış isteği ağ hatasıyla bile karşılaşsa yine de giriş formunu
        // göstermek daha güvenli tarafta durmaktır.
    }

    // BUG (bulundu/düzeltildi): lastGoodInstance temizlenmezse, "Çıkış
    // yap"ın hemen ardından × ile vazgeçmek handleAuthCancel'i BU ANDA
    // kilitli olan aynı instance'a "geri düşürür" - kapı bir an kapanıp
    // hemen yeniden açılırdı (refresh() 401 alıp showAuthGate'i tekrar
    // tetikler). Az önce çıkış yaptığımız instance artık "son iyi bilinen"
    // olamaz.
    if (state.lastGoodInstance === instance) state.lastGoodInstance = null;

    showAuthGate();
}

/**
 * Giriş formunu kapatma (×) - "vazgeçtim, sayfayı yenilemek istemiyorum"
 * demenin yolu. Kilitli instance'ta kalırsak ekranda gösterecek veri
 * olmaz (her istek 401 döner), o yüzden Sunucu seçicisini son bilinen
 * kilitsiz instance'a GERİ ALIYORUZ - kullanıcı "Prod"u yanlışlıkla
 * seçmiş/vazgeçmiş gibi davranır, tıpkı hiç seçmemiş gibi.
 */
function handleAuthCancel() {
    // lastGoodInstance BİLEREK tek başına güvenilmiyor - hâlâ mevcut
    // (kilitli) instance'a eşit çıkarsa (örn. "Çıkış yap"ın hemen ardından
    // × - bkz. handleAuthLogout'taki not) devre dışı bırakılıp gerçekten
    // giriş istemeyen bir sonraki adaya düşülüyor.
    const candidates = [
        state.lastGoodInstance,
        ...Object.values(state.instanceMeta).filter(i => !i.requiresLogin).map(i => i.name)
    ];
    const fallback = candidates.find(name => name && name !== state.instance);

    // BUG (bulundu/düzeltildi): geri düşülecek KİLİTSİZ bir instance yoksa
    // (bütün sunucular RequiresLogin ise - sahada tipik durum) eskiden yine
    // de kapı kapatılıyor, hemen ardından refresh() 401 alıp kapıyı tekrar
    // açıyordu. Kullanıcı açısından × hiçbir şey yapmıyor gibi görünüyor,
    // arkada da 2 gereksiz 401 isteği atılıyordu. Gidecek yer yoksa kapıyı
    // AÇIK bırakmak doğrusu: gösterilecek veri zaten yok.
    if (!fallback) {
        $('authError').hidden = false;
        $('authError').textContent =
            'Giriş yapmadan gösterilecek veri yok - tanımlı sunucuların hepsi giriş istiyor.';
        return;
    }

    state.instance = fallback;
    $('instanceSelect').value = fallback;
    state.lastTimelineAt = 0;

    hideAuthGate();
    updateLogoutButton(false);
    refresh();
    reloadActiveTab();
    restartTimer();
}

// ------------------------------------------------------------------
// Sunucuları yönet - appsettings.json'a hiç dokunmadan ekle/düzenle/sil.
//
// Api/InstanceManagementEndpoints.cs'in ince istemci karşılığı: liste +
// tek bir paylaşılan form (hem "yeni" hem "düzenle" için, state.imEditingKey
// ayırt eder). Şifre alanı DÜZENLEMEDE hep boş başlar - sunucu zaten
// kayıtlı şifreyi bir daha ekrana basmıyor (bkz. InstanceSummary.HasPassword),
// boş bırakılırsa mevcut şifre korunur (bkz. InstanceRegistry.AddOrUpdateAsync).
// ------------------------------------------------------------------

function instanceBadges(item) {
    const badges = [];
    if (item.isDefault) badges.push('<span class="status-pill is-good">varsayılan</span>');
    if (item.requiresLogin) badges.push('<span class="status-pill is-warn">giriş gerekli</span>');
    if (item.slackAlertsEnabled) badges.push('<span class="status-pill is-good">Slack açık</span>');
    return badges.join('');
}

function renderInstanceManagerList(items) {
    const list = $('instMgrList');

    if (items.length === 0) {
        list.innerHTML = '<p class="empty">Henüz tanımlı sunucu yok.</p>';
        return;
    }

    list.innerHTML = items.map(it => `
        <div class="instmgr-item">
            <div class="instmgr-item-info">
                <p class="instmgr-item-name">${esc(it.displayName || it.instanceKey)}
                    <span class="instmgr-item-server">(${esc(it.instanceKey)})</span></p>
                <p class="instmgr-item-server">${esc(it.server)} / ${esc(it.databaseName)}</p>
                <div class="instmgr-item-badges">${instanceBadges(it)}</div>
            </div>
            <div class="instmgr-item-actions">
                <button type="button" class="im-edit" data-key="${esc(it.instanceKey)}">Düzenle</button>
                <button type="button" class="im-delete is-danger" data-key="${esc(it.instanceKey)}">Sil</button>
            </div>
        </div>`).join('');

    list.querySelectorAll('.im-edit').forEach(btn =>
        btn.addEventListener('click', () =>
            openInstanceForm(state.instMgrItems.find(i => i.instanceKey === btn.dataset.key))));

    list.querySelectorAll('.im-delete').forEach(btn =>
        btn.addEventListener('click', () => handleInstanceDelete(btn.dataset.key)));
}

async function loadInstanceManagerList() {
    try {
        const data = await getJson('/api/settings/instances');
        state.instMgrItems = data.items || [];
        $('instMgrUnmanagedNote').hidden = data.managed;
        $('instMgrAddNew').hidden = !data.managed;
        renderInstanceManagerList(state.instMgrItems);
    } catch (err) {
        $('instMgrList').innerHTML = `<p class="empty">Yüklenemedi: ${esc(err.message)}</p>`;
    }
}

function openInstanceManager() {
    closeInstanceForm();
    $('instMgr').hidden = false;
    loadInstanceManagerList();
    loadSlackSetting();
}

// ------------------------------------------------------------------
// Slack webhook ayarı
//
// Adres BİR DAHA ekrana okunmaz - sunucu yalnızca "kayıtlı mı" der
// (bkz. Api/InstanceManagementEndpoints.cs /slack). Aynı disiplin
// sunucu şifrelerinde de var: bir kez girilir, gösterilmez.
// ------------------------------------------------------------------

function setSlackError(message) {
    const box = $('slackError');
    box.hidden = !message;
    box.textContent = message || '';
}

async function loadSlackSetting() {
    setSlackError('');
    const note = $('slackState');

    try {
        const s = await getJson('/api/settings/slack');

        if (!s.storable) {
            note.className = 'instmgr-slack-note is-off';
            note.textContent =
                'İzleme veritabanına ulaşılamadığı için webhook kaydedilemiyor.';
            return;
        }

        if (s.configured) {
            note.className = 'instmgr-slack-note is-on';
            note.textContent = s.fromDatabase
                ? 'Webhook kayıtlı — bildirimler gönderilebilir.'
                : 'Webhook appsettings.json/ortam değişkeninden geliyor. Buraya kaydedersen dosyadan silebilirsin.';
        } else {
            note.className = 'instmgr-slack-note is-off';
            note.textContent = 'Webhook tanımlı değil — Slack bildirimi kapalı.';
        }
    } catch (err) {
        note.className = 'instmgr-slack-note is-off';
        note.textContent = 'Durum okunamadı: ' + err.message;
    }
}

async function saveSlackSetting(url) {
    setSlackError('');
    try {
        const res = await fetch('/api/settings/slack', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ webhookUrl: url })
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) { setSlackError(data.error || ('HTTP ' + res.status)); return false; }

        $('slackWebhook').value = '';
        await loadSlackSetting();
        return true;
    } catch (err) {
        setSlackError('Kaydedilemedi: ' + err.message);
        return false;
    }
}

async function testSlackSetting() {
    setSlackError('');
    const btn = $('slackTest');
    btn.disabled = true;
    btn.textContent = 'Gönderiliyor…';

    try {
        const res = await fetch('/api/settings/slack/test', { method: 'POST' });
        const data = await res.json().catch(() => ({}));
        if (res.ok) {
            $('slackState').className = 'instmgr-slack-note is-on';
            $('slackState').textContent = 'Test mesajı gönderildi — Slack kanalını kontrol et.';
        } else {
            setSlackError(data.error || ('Gönderilemedi: HTTP ' + res.status));
        }
    } catch (err) {
        setSlackError('Gönderilemedi: ' + err.message);
    } finally {
        btn.disabled = false;
        btn.textContent = 'Test mesajı gönder';
    }
}

function closeInstanceManager() {
    $('instMgr').hidden = true;
    closeInstanceForm();
}

function toggleImCredFields() {
    $('imCredFields').hidden = $('imRequiresLogin').checked;
}

/** item=null → yeni sunucu formu; item verilirse o kaydı düzenleme formu. */
function openInstanceForm(item) {
    state.imEditingKey = item ? item.instanceKey : null;

    $('instMgrFormTitle').textContent = item
        ? `Düzenle: ${item.displayName || item.instanceKey}`
        : 'Yeni sunucu';

    $('imKey').value = item ? item.instanceKey : '';
    $('imKey').disabled = !!item;   // anahtar oluşturulduktan sonra değiştirilemez, bkz. InstanceManagementEndpoints üstündeki not
    $('imDisplayName').value = (item && item.displayName) || '';
    $('imServer').value = (item && item.server) || '';
    // Veritabanı alanı formdan KALDIRILDI: bağlantı yalnızca "bir yere
    // bağlanmak" için açılıyor, okunan her şey sunucu geneli DMV -
    // master dışında bir şey seçmenin pratikte faydası yoktu, sorusu
    // ise her yeni sunucuda tekrar tekrar karşımıza çıkıyordu.
    // Sunucu tarafı boş gelen değeri zaten "master" yapıyor
    // (bkz. Api/InstanceManagementEndpoints.cs).
    $('imRequiresLogin').checked = !!(item && item.requiresLogin);
    $('imUserId').value = (item && item.userId) || '';
    $('imPassword').value = '';
    $('imPassword').placeholder = item && item.hasPassword ? 'değiştirmek istemiyorsan boş bırak' : 'şifre';
    $('imIsDefault').checked = !!(item && item.isDefault);
    $('imAutoDiscover').checked = item ? !!item.autoDiscoverAgReplicas : true;
    $('imReplicaOverrides').value = (item && item.replicaHostOverrides) || '';
    $('imSlackAlerts').checked = !!(item && item.slackAlertsEnabled);
    $('imError').hidden = true;

    toggleImCredFields();
    $('instMgrForm').hidden = false;
    $('imKey').focus();
}

function closeInstanceForm() {
    $('instMgrForm').hidden = true;
    state.imEditingKey = null;
}

/**
 * BUG (bulundu/düzeltildi): instance değiştiğinde (seçiciden ya da
 * ekle/düzenle/sil sonrası) yalnızca refresh() çağrılıyordu - o
 * sadece Genel Bakış'ın snapshot'ını günceller. Kullanıcı o sırada
 * "Stored Procedure"/"Index Analizi"/"Always On"/"Agent Jobs"
 * sekmelerinden birindeyse, "yüklendi" bayrağı sıfırlansa bile hiçbir
 * şey o sekmeyi yeniden ÇEKMİYORDU - ekranda ESKİ instance'ın verisi
 * asılı kalıyordu, ta ki kullanıcı sekmeye tekrar tıklayana kadar.
 * Şu an açık olan sekme neyse onu da tazeliyoruz.
 */
function reloadActiveTab() {
    if (!state.instance) return;              // sunucu yok - hiçbir sekme veri çekmesin

    const activeTab = document.querySelector('.tab.is-active');
    if (!activeTab) return;

    switch (activeTab.dataset.tab) {
        case 'indexanalysis':
            loadMissingIndexes({ force: true });
            break;
        case 'storedproc':
            loadTopProcedures(state.spSort, state.spDir, { force: true });
            loadUnusedProcedures({ force: true });
            break;
        case 'alwayson':
            loadAlwaysOn({ force: true });
            break;
        case 'agentjobs':
            loadAgentJobs({ force: true });
            break;
        // 'overview'/'activity': refresh() zaten yeterli, ayrı bir uçları yok.
    }

    // Bekleme trendi Genel Bakış'ta yaşıyor ama o sekmeye tıklama olayı
    // yok (varsayılan olarak zaten açık) - bu yüzden switch'in dışında,
    // her instance değişiminde ayrıca tetiklenmesi gerekiyor.
    loadWaitTrend({ force: true });
}

/** Ekle/düzenle/sil sonrası: üstteki seçiciyi, sekme "yüklendi" bayraklarını ve ekranı tazeler. */
async function afterInstanceListChanged() {
    await refreshInstanceOptions();
    if (!$('instMgr').hidden) await loadInstanceManagerList();

    state.miLoaded = false;
    state.spLoaded = false;
    state.unusedLoaded = false;
    state.agLoaded = false;
    state.jobsLoaded = false;
    state.waitTrendLoaded = false;
    state.lastTimelineAt = 0;

    // Son sunucu da silinmiş olabilir - o zaman döngüyü durdurup
    // ekranı "sunucu ekle" durumuna alıyoruz, boşa istek atmıyoruz.
    if (!state.instance) {
        enterNoInstanceState();
        return;
    }

    const ok = await ensureAuthorized();
    if (ok) {
        refresh();
        reloadActiveTab();
        // Yeni sunucu eklendiyse döngü durmuş olabilir; yeniden kur.
        restartTimer();
    }
}

async function handleInstanceFormSubmit(e) {
    e.preventDefault();

    const isNew = !state.imEditingKey;
    const key = isNew ? $('imKey').value.trim() : state.imEditingKey;

    const body = {
        displayName: $('imDisplayName').value.trim(),
        server: $('imServer').value.trim(),
        databaseName: '',            // sunucu tarafı "master" yapıyor
        userId: $('imUserId').value.trim(),
        password: $('imPassword').value,
        requiresLogin: $('imRequiresLogin').checked,
        isDefault: $('imIsDefault').checked,
        autoDiscoverAgReplicas: $('imAutoDiscover').checked,
        replicaHostOverrides: $('imReplicaOverrides').value.trim(),
        slackAlertsEnabled: $('imSlackAlerts').checked
    };
    if (isNew) body.instanceKey = key;

    const err = $('imError');
    err.hidden = true;
    const btn = $('imSave');
    btn.disabled = true;

    try {
        const url = isNew
            ? '/api/settings/instances'
            : '/api/settings/instances/' + encodeURIComponent(key);

        const res = await fetch(url, {
            method: isNew ? 'POST' : 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        const data = await res.json().catch(() => ({}));

        if (!res.ok) {
            err.textContent = data.error || ('HTTP ' + res.status);
            err.hidden = false;
            return;
        }

        closeInstanceForm();

        // Yeni bir sunucu eklendiyse hemen ONA geçip ekranı kapatıyoruz -
        // "kaydettim" yazıp hiçbir şey değişmeden Sunucular ekranında
        // kalmak, bağlantının gerçekten çalışıp çalışmadığını görmeyi
        // güçleştiriyordu. Artık kaydet → hemen o sunucunun canlı verisi
        // ekranda: bağlantı bilgisi yanlışsa da bunu hemen (hata olarak) görür.
        if (isNew) {
            closeInstanceManager();
            state.instance = key;
            $('instanceSelect').value = key;
            state.lastTimelineAt = 0;
        }

        await afterInstanceListChanged();
    } catch (ex) {
        err.textContent = 'Kaydedilemedi: ' + ex.message;
        err.hidden = false;
    } finally {
        btn.disabled = false;
    }
}

async function handleInstanceDelete(key) {
    const item = state.instMgrItems.find(i => i.instanceKey === key);
    const label = (item && (item.displayName || item.instanceKey)) || key;

    if (!window.confirm(
        `'${label}' sunucusunu izleme listesinden çıkarmak istediğine emin misin?\n\n` +
        `Geçmiş trend/olay verisi saklı kalır, yalnızca izleme listesinden çıkar.`
    )) return;

    try {
        const res = await fetch('/api/settings/instances/' + encodeURIComponent(key), { method: 'DELETE' });
        const data = await res.json().catch(() => ({}));

        if (!res.ok) {
            window.alert(data.error || ('Silinemedi: HTTP ' + res.status));
            return;
        }

        await afterInstanceListChanged();
    } catch (ex) {
        window.alert('Silinemedi: ' + ex.message);
    }
}

/**
 * Tanımlı hiç sunucu kalmadıysa (hepsi silindiyse ya da ilk açılış)
 * ekranı "yapılacak iş" durumuna alır ve DÖNGÜYÜ DURDURUR.
 *
 * BUG (bulundu/düzeltildi): eskiden yalnızca state.instance null'a
 * düşüyordu ama zamanlayıcı çalışmaya devam ediyordu; refresh() her
 * turda "?instance=" ile istek atıp 404 alıyor, ekranda da silinmiş
 * sunucunun BAYAT verisi duruyordu. Saniyede bir boşa giden istek,
 * üstelik kullanıcıya "hâlâ bir şey izleniyor" hissi veren yanlış bir
 * ekran - ikisi de kabul edilemez.
 */
function enterNoInstanceState() {
    if (state.timer) { clearInterval(state.timer); state.timer = null; }

    state.lastSnapshot = null;

    const box = $('errors');
    box.hidden = false;
    box.classList.add('is-notice');
    box.textContent =
        'İzlenecek sunucu tanımlı değil — sağ üstteki "⚙ Sunucular" ekranından ekleyebilirsin.';

    $('lastUpdate').textContent = 'duraklatıldı';
}

async function refresh() {
    // Sunucu yoksa sunucuya sormanın anlamı yok.
    if (!state.instance) { enterNoInstanceState(); return; }

    // Yavaş bir sunucuda istekler üst üste binmesin.
    if (state.inFlight) return;
    state.inFlight = true;

    try {
        const url = '/api/live/snapshot?instance=' + encodeURIComponent(state.instance || '');
        const snapshot = await getJson(url);

        state.lastSnapshot = snapshot;
        render(snapshot);
        showErrors(snapshot.errors);

        $('lastUpdate').textContent =
            clockTime(snapshot.capturedAtUtc) + ' · ' + snapshot.elapsedMs + ' ms';

        blip();
        loadTimeline();
    } catch (err) {
        showErrors([{ panel: 'bağlantı', message: err.message }]);
        $('lastUpdate').textContent = 'güncellenemedi';
    } finally {
        state.inFlight = false;
    }
}

// Zaman çizelgesi 24 saatlik geçmişi çeker ve arkasındaki veriyi
// yalnızca toplayıcı üretir - varsayılan olarak 30 saniyede bir. Panel
// saniyede bir yenilenirken bunu her turda çekmek, aynı cevabı 30 kez
// istemek demektir: izleme DB'sine bedava olmayan, tamamen boş bir yük.
// O yüzden çizelgenin kendi ritmi var, snapshot'a bağlı değil.
const TIMELINE_MIN_INTERVAL_MS = 30000;

async function loadTimeline(force = false) {
    const now = Date.now();
    if (!force && now - state.lastTimelineAt < TIMELINE_MIN_INTERVAL_MS) return;

    state.lastTimelineAt = now;

    try {
        const url = '/api/live/timeline?instance=' + encodeURIComponent(state.instance || '');
        renderTimeline(await getJson(url));
    } catch {
        // Zaman çizelgesi ikincil bir panel; sessizce boş kalması yeterli.
        // Ama başarısız denemeyi "yaptım" saymıyoruz: sayacı geri alıyoruz
        // ki bir sonraki turda yeniden denensin.
        state.lastTimelineAt = 0;
    }
}

function blip() {
    const pulse = $('pulse');
    pulse.classList.remove('is-live');
    void pulse.offsetWidth;   // animasyonu yeniden tetikle
    pulse.classList.add('is-live');
}

function showErrors(errors) {
    const box = $('errors');
    box.classList.remove('is-notice');   // "sunucu yok" bildirimi varsa kalksın
    if (!errors || errors.length === 0) {
        box.hidden = true;
        return;
    }
    box.hidden = false;
    box.textContent = 'Okunamayan paneller — ' +
        errors.map(e => `${e.panel}: ${e.message}`).join(' | ');
}

// ------------------------------------------------------------------
// Çizim
// ------------------------------------------------------------------

function render(s) {
    renderVerdict(s);
    renderTrendWindow(s.trend);
    renderKpis(s.kpis);
    renderChecks(s.health);
    renderCpu(s.cpu);
    renderMemory(s.memory);
    renderTempDb(s.tempDb);
    renderDisk(s.disk);
    renderWaits(s.waits);
    renderBlocking(s.blocking);
    renderRequests(s.activity);
}

function renderVerdict(s) {
    $('verdictServer').textContent = s.server.serverName || s.instanceName;

    const checks = s.health.checks || [];
    const open = checks.filter(c => c.scoreImpact < 0);
    const critical = open.filter(c => c.severity === 'Critical').length;
    const warning = open.length - critical;
    const healthy = checks.length - open.length;

    $('verdictLine').textContent = open.length === 0
        ? 'Bütün kontroller temiz.'
        : critical > 0
            ? `${critical} kritik bulgu var, önce onlara bak.`
            : 'Kritik yok, açık uyarılar var.';

    // Etiketler: sayilari renkli rozetlere tasiyarak tek bakista okunur hale getiriyoruz.
    $('verdictTags').innerHTML = [
        critical > 0 ? `<span class="tag is-critical">${critical} kritik</span>` : '',
        warning  > 0 ? `<span class="tag is-warning">${warning} uyarı</span>` : '',
        healthy  > 0 ? `<span class="tag is-healthy">${healthy} sağlıklı</span>` : '',
        `<span class="tag">${esc(s.server.edition || '')}</span>`,
        `<span class="tag">${s.server.cpuCount} çekirdek · ${s.server.schedulerCount} scheduler</span>`,
        `<span class="tag">${num(s.server.physicalMemoryGb, 0)} GB RAM</span>`,
        `<span class="tag">${num(s.server.uptimeHours, 0)} saat açık</span>`,
        `<span class="tag">${s.server.onlineDatabaseCount}/${s.server.databaseCount} DB online</span>`
    ].join('');

    // Skor halkasi: dasharray ile dolan cember.
    const score = s.health.score;
    const circumference = 2 * Math.PI * 50;   // r = 50
    const filled = (score / 100) * circumference;
    const color = score >= 85 ? '#4ade9b' : score >= 60 ? '#f7bd63' : '#ff7a72';

    const arc = $('gaugeArc');
    arc.setAttribute('stroke', color);
    arc.setAttribute('stroke-dasharray', `${filled.toFixed(1)} ${(circumference - filled).toFixed(1)}`);

    $('scoreValue').textContent = score;

    const parts = [
        ['performans',    s.health.performanceScore, '#6d97ff'],
        ['güvenilirlik',  s.health.reliabilityScore, '#4ade9b'],
        ['kapasite',      s.health.capacityScore,    '#4fd0da']
    ];
    $('scoreBreakdown').innerHTML = parts.map(([name, value, c]) => `
        <div class="sb-row">
            <span>${name}</span>
            <span class="sb-bar"><i style="width:${value}%;background:${c}"></i></span>
            <span class="sb-num">${value}</span>
        </div>`).join('');

    // "Once neye bakayim" — en cok puan goturen madde.
    const first = open[0];
    $('verdictNext').innerHTML = first
        ? `<p class="next-label">Önce buna bak</p>
           <p class="next-title">${esc(first.question)}</p>
           <p class="next-detail">${esc(first.finding)}</p>`
        : `<p class="next-label">Bekleyen iş</p>
           <p class="next-title">Yok</p>
           <p class="next-detail">Açık bulgu bulunmuyor.</p>`;
}

/** Dakikayı okunur süreye çevirir: "45 dk", "2 sa", "1 sa 20 dk". */
function spanLabel(minutes) {
    if (minutes < 1) return '1 dk\'dan kısa';
    if (minutes < 60) return minutes + ' dk';

    const h = Math.floor(minutes / 60);
    const m = minutes % 60;
    return m === 0 ? h + ' sa' : h + ' sa ' + m + ' dk';
}

/*
    Grafiklerin kapsadığı süreyi yazan satır.

    Bu etiket olmadan mini grafikler cevapsız bir soru bırakıyordu:
    1 saat mi, 1 gün mü? Süre sabit de değil - "son 40 örnek" isteniyor,
    örnekleri toplayıcı üretiyor. Normalde 40 x 30 sn = 20 dakika eder,
    ama toplayıcı durmuşsa aynı 40 örnek çok daha geniş bir aralığa
    yayılır. O yüzden metni sunucudan gelen GERÇEK zaman damgalarından
    kuruyoruz; sabit yazsaydık tam da aksama anında yanlış olurdu.
*/
function renderTrendWindow(trend) {
    const el = $('kpiWindow');
    if (!el) return;

    if (!trend || !trend.points || trend.points < 2 || !trend.fromUtc) {
        el.textContent = 'grafikler · geçmiş henüz birikmedi';
        el.removeAttribute('title');
        return;
    }

    const span = spanLabel(trend.spanMinutes);
    const stepSec = Math.round((trend.spanMinutes * 60) / (trend.points - 1));

    el.textContent = `grafikler · son ${span} · ${trend.points} örnek`;
    el.title =
        `${dayTime(trend.fromUtc)} → ${dayTime(trend.toUtc)}\n` +
        `${trend.points} örnek, yaklaşık ${stepSec} sn aralıkla\n` +
        `Örnekleri arka plandaki toplayıcı üretir; panelin yenilenme sıklığından bağımsızdır.`;
}

/*
    Pencerenin tepe değeri.

    Burada önce "başlangıca göre % değişim" denendi ve KÖTÜ bir fikirdi:
    CPU %1'den %3'e çıkınca "▲ %200" yazıyordu. Teknik olarak doğru,
    pratikte yanıltıcı. Üstelik yüzde, pencerenin nerede başladığına
    bağlıdır - yani ölçtüğü şey sunucu değil, grafiğin sol kenarıdır.

    Tepe değeri ise kararlıdır ve tek başına iş görür: yanındaki anlık
    sayıyla birleşince "şu an zirvede miyim, dipte miyim" sorusu tek
    bakışta cevaplanır.

    Sabit serilerde (hep 0 olan deadlock gibi) yazılmıyor; tepe zaten
    anlık değere eşit, tekrar etmenin anlamı yok.
*/
function kpiPeak(values) {
    if (!values || values.length < 2) return '';

    const max = Math.max(...values);
    const min = Math.min(...values);
    if (max === min) return '';

    // Birim bilerek yazılmıyor: hemen altındaki ana değer zaten taşıyor.
    // "MAX 7.442sn" gibi bitişik bir metin, kazandırdığından çok yer yer.
    return `<span class="kpi-max" title="son pencerenin en yüksek değeri">` +
           `MAX ${num(max, max < 100 ? 1 : 0)}</span>`;
}

/*
    Gösterge kimlik renkleri.

    Dokuz sparkline'ın dokuzu da aynı maviyken şerit tek bir gri blok
    gibi okunuyordu. Renkleri KONUYA göre grupladık: iş hacmi mavi,
    CPU mor, bellek turkuaz, eşzamanlılık magenta. Böylece göz şeridi
    dört kümeye ayırıyor ve aradığı göstergeyi okumadan buluyor.

    Yeşil, sarı ve kırmızı bilerek YOK: bu ekranda o üç renk durumdur.
    Kimlik rengi olarak kullansaydık "yeşil sparkline" ile "sağlıklı"
    birbirine karışırdı. Uyarı/kritik durumda zaten kimlik rengi
    bırakılıp durum rengine geçiliyor - durum her zaman kimliği yener.
*/
const KPI_ACCENT = {
    batch_req_sec: '#3f6fe0',   // iş hacmi
    tran_sec:      '#3f6fe0',
    cpu_sql:       '#7c5ce0',   // CPU
    cpu_system:    '#7c5ce0',
    buffer_hit:    '#0f9aa8',   // bellek
    ple:           '#0f9aa8',
    connections:   '#d1477e',   // bağlantı ve eşzamanlılık
    blocked:       '#d1477e',
    deadlock_sec:  '#d1477e'
};

function kpiAccent(key, severity) {
    if (severity === 'Critical') return '#db3b34';
    if (severity === 'Warning')  return '#d98216';
    return KPI_ACCENT[key] || '#3f6fe0';
}

function renderKpis(kpis) {
    // Şerit saniyede bir yeniden kuruluyor; eski render'a ait nokta/kılavuz
    // zaten DOM'dan gidiyor ama ayrı yaşayan #sparkTip öyle değil - o
    // silinmezse fare kıpırdamadan bayat bir değeri göstermeye devam eder.
    hideSparkHover();

    $('kpis').innerHTML = (kpis || []).map(k => {
        const accent = kpiAccent(k.key, k.severity);
        return `
        <div class="kpi ${sevClass(k.severity)}" style="--accent:${accent}"
             ${k.note ? `title="${esc(k.note)}"` : ''}>
            <div class="kpi-top">
                <p class="kpi-label">${esc(k.label)}</p>
                ${kpiPeak(k.trend)}
            </div>
            <p class="kpi-value">${num(k.value, 2)}<span class="kpi-unit">${esc(k.unit)}</span></p>
            <div class="kpi-graph">${sparkline(k.trend, accent)}</div>
        </div>`;
    }).join('');
}

/**
 * Alan grafigi. Cizgi tek basina "su an ne" der; altindaki dolgu
 * "nereden geldi" der. Izleme ekraninda ikincisi cogu zaman daha
 * kiymetli, o yuzden duz cizgi yerine dolgulu alan kullaniyoruz.
 */
let sparkSeq = 0;
/** Rengi çağıran belirler: kimlik rengi mi, durum rengi mi kararı orada verilir. */
function sparkline(values, color) {
    if (!values || values.length < 2) {
        return '<p class="spark-empty">geçmiş bekleniyor</p>';
    }

    const min = Math.min(...values);
    const max = Math.max(...values);

    // Hiç değişmeyen seri (sürekli 0 olan deadlock gibi) eskiden en dipte
    // düz bir çizgi olarak çiziliyordu ve "dibe vurmuş" gibi görünüyordu.
    // Sabit bir seriyi ortada çizmek daha dürüst: dip değil, düz.
    const flat = max === min;
    const span = flat ? 1 : (max - min);

    const w = 100, h = 34, pad = 4;
    const id = 'sg' + (++sparkSeq);

    const pts = values.map((v, i) => {
        const x = (i / (values.length - 1)) * w;
        const y = flat ? h / 2 : h - pad - ((v - min) / span) * (h - pad * 2);
        return [x, y];
    });

    const line = 'M' + pts.map(p => `${p[0].toFixed(1)},${p[1].toFixed(1)}`).join('L');
    const area = line + `L${w},${h}L0,${h}Z`;

    // En düşük / en yüksek değeri ipucuna yazıyoruz: grafik "yükseliyor mu"
    // sorusunu cevaplar, tepe değerini ise ancak sayı söyleyebilir.
    const tip = flat
        ? `sabit: ${num(max, 2)}`
        : `en düşük ${num(min, 2)} · en yüksek ${num(max, 2)}`;

    return `<svg class="spark" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none"
                 role="img" aria-label="${esc(tip)}" data-trend="${values.join(',')}"
                 data-color="${color}"><title>${esc(tip)}</title>
        <defs>
            <linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%"   stop-color="${color}" stop-opacity=".4"/>
                <stop offset="100%" stop-color="${color}" stop-opacity="0"/>
            </linearGradient>
        </defs>
        <path d="${area}" fill="url(#${id})"/>
        <path d="${line}" fill="none" stroke="${color}" stroke-width="1.6"
              stroke-linejoin="round" stroke-linecap="round"
              vector-effect="non-scaling-stroke"/>
    </svg>`;
}

/*
    Sparkline hover: dikey kılavuz çizgi + noktalı işaretçi + değer ipucu.

    dataviz kuralı net: "grafiği olan her stat tile, çıplak-tile'ların
    aksine hover katmanı alır." Buraya kadar grafik sadece bakılan, artık
    sorgulanabilen bir şey - "üç örnek önce ne kadardı" sorusunun cevabı
    fareyi gezdirmekle geliyor.

    SVG viewBox 0 0 100 34 ve preserveAspectRatio="none" olduğu için
    fare pozisyonundan viewBox koordinatına dönüşüm DOĞRUSAL - en/boy
    oranı düzeltmesi gerekmiyor, tek yapılan oranı 100/34 ile çarpmak.
*/
const SPARK_W = 100, SPARK_H = 34, SPARK_PAD = 4;

function sparkPointAt(values, index) {
    const min = Math.min(...values);
    const max = Math.max(...values);
    const flat = max === min;
    const span = flat ? 1 : (max - min);
    const x = (index / (values.length - 1)) * SPARK_W;
    const y = flat ? SPARK_H / 2
                    : SPARK_H - SPARK_PAD - ((values[index] - min) / span) * (SPARK_H - 2 * SPARK_PAD);
    return { x, y };
}

/** Nokta index'inden yaklaşık saat: pencere sınırları arasında eşit aralıklı kabul edilir. */
function sparkTimeAt(index, count) {
    const trend = state.lastSnapshot && state.lastSnapshot.trend;
    if (!trend || !trend.fromUtc || count < 2) return null;

    const from = new Date(trend.fromUtc.endsWith('Z') ? trend.fromUtc : trend.fromUtc + 'Z');
    const stepMs = (trend.spanMinutes * 60000) / (count - 1);
    return new Date(from.getTime() + index * stepMs);
}

function handleSparkHover(e) {
    const svg = e.target.closest('.spark');
    if (!svg) { hideSparkHover(); return; }

    const raw = svg.dataset.trend;
    if (!raw) return;
    const values = raw.split(',').map(Number);
    if (values.length < 2) return;

    const rect = svg.getBoundingClientRect();
    const fraction = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
    const index = Math.round(fraction * (values.length - 1));
    const { x, y } = sparkPointAt(values, index);
    const color = svg.dataset.color || '#3f6fe0';

    // Nokta + kılavuz SVG'nin İÇİNDE yaşıyor (ayrı bir DOM katmanı değil) -
    // vektör grafiğin bir parçası gibi kırışıksız görünür, sayfa scroll'unda
    // yeniden konumlandırma hesabı gerektirmez.
    let dot = svg.querySelector('.spark-dot');
    let guide = svg.querySelector('.spark-guide');
    if (!dot) {
        guide = document.createElementNS('http://www.w3.org/2000/svg', 'line');
        guide.setAttribute('class', 'spark-guide');
        guide.setAttribute('y1', '0');
        guide.setAttribute('y2', String(SPARK_H));
        svg.appendChild(guide);

        dot = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        dot.setAttribute('class', 'spark-dot');
        dot.setAttribute('r', '2.6');
        svg.appendChild(dot);
    }
    guide.setAttribute('x1', x.toFixed(1));
    guide.setAttribute('x2', x.toFixed(1));
    guide.setAttribute('stroke', color);
    dot.setAttribute('cx', x.toFixed(1));
    dot.setAttribute('cy', y.toFixed(1));
    dot.setAttribute('fill', color);

    const time = sparkTimeAt(index, values.length);
    const tip = $('sparkTip');
    tip.innerHTML = `<b>${num(values[index], 2)}</b>` +
        (time ? `<span>${clockTime(time.toISOString())}</span>` : '');
    tip.hidden = false;

    // Tooltip fareyle birlikte gezer, üstte durur; sağ kenara taşmasın
    // diye viewport genişliğine göre kelepçeleniyor.
    const tipWidth = 92;
    const left = Math.min(window.innerWidth - tipWidth - 8, Math.max(8, e.clientX - tipWidth / 2));
    tip.style.left = left + 'px';
    tip.style.top = (rect.top + window.scrollY - 8) + 'px';
}

function hideSparkHover() {
    $('sparkTip').hidden = true;
    document.querySelectorAll('.spark-dot, .spark-guide').forEach(el => el.remove());
}

/** Donut dilimi cizer. stroke-dasharray ile; ayri path hesabi gerekmiyor. */
/*
    Renk her zaman style="stroke:..." ÜZERİNDEN veriliyor, XML stroke=""
    özniteliği üzerinden DEĞİL. Sebebi: aşağıdaki "boş" durumda ve "Boş"
    diliminde var(--rule) kullanıyoruz - tema açık/koyu arasında
    değiştiğinde bu iki dilimin de arka planla uyumlu kalması gerekiyor,
    çıplak bir XML özniteliği var()'ı çözümlemez ama style bir CSS
    değeridir, çözümler.
*/
function donut(slices, size = 108) {
    const total = slices.reduce((sum, s) => sum + s.value, 0);
    if (total <= 0) {
        return `<svg class="donut" viewBox="0 0 ${size} ${size}">
            <circle cx="${size/2}" cy="${size/2}" r="${size/2-9}" style="stroke:var(--rule)"/>
        </svg>`;
    }

    const r = size / 2 - 9;
    const circumference = 2 * Math.PI * r;
    let offset = 0;

    const arcs = slices.map(sl => {
        const len = (sl.value / total) * circumference;
        const arc = `<circle cx="${size/2}" cy="${size/2}" r="${r}"
                style="stroke:${sl.color}"
                stroke-dasharray="${len.toFixed(2)} ${(circumference - len).toFixed(2)}"
                stroke-dashoffset="${(-offset).toFixed(2)}"/>`;
        offset += len;
        return arc;
    }).join('');

    return `<svg class="donut" viewBox="0 0 ${size} ${size}"
                 style="transform:rotate(-90deg)" aria-hidden="true">${arcs}</svg>`;
}

/**
 * Durum bir ikonla gelir, hiçbir zaman yalnız renkle değil - renk körü bir
 * kullanıcı için "yeşil mi kırmızı mı" ayrımı tek başına yeterli değildir,
 * şekil de (tik / üçgen / çarpı) aynı bilgiyi taşımalı.
 */
function sevIcon(severity) {
    const s = String(severity || 'unknown').toLowerCase();
    const paths = {
        healthy: '<path d="M7 12.5 10.2 15.5 17 8.5"/>',
        warning: '<path d="M12 3 2 20h20L12 3Z" stroke-linejoin="round"/><path d="M12 10v4.5"/><circle cx="12" cy="17.3" r=".9" fill="currentColor" stroke="none"/>',
        critical: '<circle cx="12" cy="12" r="9"/><path d="M8.5 8.5 15.5 15.5M15.5 8.5 8.5 15.5"/>',
        unknown: '<circle cx="12" cy="12" r="9"/><path d="M9.3 9.5a2.7 2.7 0 1 1 3.9 2.4c-.9.5-1.2 1-1.2 2"/><circle cx="12" cy="16.3" r=".9" fill="currentColor" stroke="none"/>'
    };
    return `<svg class="check-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                 stroke-width="2" stroke-linecap="round" aria-hidden="true">${paths[s] || paths.unknown}</svg>`;
}

function renderChecks(health) {
    const checks = health.checks || [];
    $('checks').innerHTML = checks.map(c => `
        <li class="check ${sevClass(c.severity)}">
            <div>
                <div class="check-head">
                    ${sevIcon(c.severity)}
                    <p class="check-question">${esc(c.question)}</p>
                </div>
                <p class="check-finding">${esc(c.finding)}</p>
                ${c.remedy ? `<p class="check-remedy">${esc(c.remedy)}</p>` : ''}
            </div>
            <p class="check-impact">${c.scoreImpact < 0 ? c.scoreImpact + ' puan' : sevLabel(c.severity)}</p>
        </li>
    `).join('');
}

/** Ortak ölçüm bloğu: isim, sayı, çubuk. */
function metric(name, value, unit, percent, severity) {
    const width = Math.max(0, Math.min(100, percent ?? 0));
    return `
        <div class="metric">
            <div class="metric-top">
                <span class="metric-name">${esc(name)}</span>
                <span class="metric-number ${sevClass(severity)}">${value}<span class="kpi-unit">${esc(unit || '')}</span></span>
            </div>
            <div class="bar"><i class="${sevClass(severity)}" style="width:${width}%"></i></div>
        </div>`;
}

function badge(id, severity) {
    const el = $(id);
    el.className = 'badge ' + sevClass(severity);
    el.textContent = sevLabel(severity);
}

function renderCpu(cpu) {
    badge('cpuBadge', cpu.severity);
    $('cpuBody').innerHTML =
        metric('SQL Server süreci', num(cpu.sqlCpuPercent), '%', cpu.sqlCpuPercent, cpu.severity) +
        metric('Sistem geneli', num(cpu.systemCpuPercent), '%', cpu.systemCpuPercent, 'healthy') +
        `<div class="subfacts">
            <span>SQL dışı süreçler</span><b>%${num(cpu.otherCpuPercent)}</b>
            <span>Sıradaki görev</span><b>${num(cpu.runnableTasks)} / ${num(cpu.schedulerCount)}</b>
         </div>`;
}

function renderMemory(mem) {
    badge('memoryBadge', mem.severity);

    // PLE'nin doğal bir üst sınırı yok; çubuğu 2000 sn referansına göre
    // dolduruyoruz - bu bir ölçek tercihi, mutlak bir eşik değil.
    const plePercent = Math.min(100, (mem.pageLifeExpectancy / 2000) * 100);

    $('memoryBody').innerHTML =
        metric('Page life expectancy', num(mem.pageLifeExpectancy), 'sn', plePercent, mem.severity) +
        metric('Buffer cache hit', num(mem.bufferCacheHitRatio, 2), '%', mem.bufferCacheHitRatio, 'healthy') +
        `<div class="subfacts">
            <span>Bekleyen grant</span><b>${num(mem.pendingGrants)}</b>
            <span>Kullanılan / hedef</span><b>${num(mem.committedGb, 1)} / ${num(mem.targetGb, 1)} GB</b>
            <span>Fiziksel RAM</span><b>${num(mem.physicalGb, 1)} GB</b>
         </div>`;
}

function renderTempDb(t) {
    badge('tempdbBadge', t.severity);

    // Dagilim onemli: doluluk kullanici nesnelerinden geliyorsa gecici
    // tablo kullanan bir sorgu, version store'dan geliyorsa acik kalmis
    // uzun bir transaction, ic nesnelerden geliyorsa spill yapan bir
    // siralama demektir. Ucu uc ayri problem, o yuzden ayri gosteriyoruz.
    const free = Math.max(0, t.totalMb - t.usedMb);
    const slices = [
        { name: 'Kullanıcı nesneleri', value: t.userObjectsMb,     color: '#0f9aa8' },
        { name: 'İç nesneler',         value: t.internalObjectsMb, color: '#4fd0da' },
        { name: 'Version store',       value: t.versionStoreMb,    color: '#7c5ce0' },
        { name: 'Boş',                 value: free,                color: 'var(--rule)' }
    ];

    const legend = slices.map(sl => `
        <div class="legend-item">
            <span class="legend-swatch" style="background:${sl.color}"></span>
            <span class="legend-name">${sl.name}</span>
            <span class="legend-value">${num(sl.value)} MB</span>
        </div>`).join('');

    $('tempdbBody').innerHTML =
        metric('Kullanım', num(t.usedPercent, 1), '%', t.usedPercent, t.severity) +
        `<div class="donut-row" style="margin-top:14px">
            ${donut(slices)}
            <div class="legend">${legend}</div>
         </div>
         <div class="subfacts">
            <span>Toplam boyut</span><b>${num(t.totalMb)} MB</b>
            <span>Ayırma çekişmesi</span><b>${num(t.allocationContention)}</b>
            <span>Veri dosyası</span><b>${num(t.dataFileCount)}</b>
         </div>`;
}

/**
 * disk.forecast yalnızca "ui" kapsamındaki snapshot'larda dolu gelir
 * (bkz. LiveMonitorService.ReadDiskAsync) - en az 3 günlük geçmiş yoksa
 * hasEnoughHistory false'tur ve burada hiçbir şey basılmaz.
 */
function diskForecastHtml(forecast) {
    if (!forecast || !forecast.hasEnoughHistory) return '';

    if (forecast.daysToFull != null) {
        const warn = forecast.daysToFull <= 30;
        return `<p class="disk-forecast${warn ? ' is-warn' : ''}">
            Bu gidişle ~${num(forecast.daysToFull)} güne dolar
            <span class="disk-forecast-note">(${forecast.historyDays} günlük geçmişe göre)</span></p>`;
    }

    return `<p class="disk-forecast">Belirgin bir büyüme trendi yok
        <span class="disk-forecast-note">(${forecast.historyDays} günlük geçmişe göre)</span></p>`;
}

function renderDisk(disk) {
    badge('diskBadge', disk.severity);
    const volumes = disk.volumes || [];

    $('diskBody').innerHTML = (volumes.length === 0
        ? '<p class="empty">Volume bilgisi yok.</p>'
        : volumes.map(v =>
            metric(v.mount, num(v.usedPercent, 1), '%', v.usedPercent, v.severity) +
            `<div class="subfacts"><span>Boş alan</span><b>${num(v.freeGb, 1)} / ${num(v.totalGb, 1)} GB</b></div>`
          ).join('')) + diskForecastHtml(disk.forecast);
}

/**
 * "Bekleme" kartı - metric() helper'ını BİLEREK kullanmıyor: o, çubuk
 * rengini severity'den (iyi/orta/kötü) alıyor, ama burada "en çok
 * bekleneni" göstermek tek başına bir sorun değil - kartın kendi kimlik
 * rengini (pembe, is-wait) taşıyan ayrı, sade bir liste yeterli.
 */
function renderWaits(w) {
    const top = (w && w.top) || [];

    $('waitsBody').innerHTML = top.length === 0
        ? '<p class="empty">Henüz yeterli örnek yok.</p>'
        : top.map(t => `
            <div class="metric">
                <div class="metric-top">
                    <span class="metric-name" title="${esc(t.waitType)}">${esc(t.waitType)}</span>
                    <span class="metric-number">${num(t.msPerSecond, 1)}<span class="kpi-unit"> ms/sn</span></span>
                </div>
                <div class="bar"><i class="is-wait" style="width:${Math.max(0, Math.min(100, t.sharePercent))}%"></i></div>
            </div>`).join('');
}

/**
 * mon.WaitSample geçmişinden bekleme türü başına ms/sn trendi - snapshot'a
 * binmiyor (bkz. Services/MetricStore.cs GetWaitTrendAsync), Genel Bakış
 * ilk açıldığında bir kez ve instance değiştiğinde tekrar yüklenir. Kendi
 * "yenile" düğmesi yok: geçmiş veri saniyeler içinde anlamlı değişmez.
 */
async function loadWaitTrend(opts = {}) {
    if (!state.instance) return;              // sunucu yok - sormanın anlamı yok
    if (state.waitTrendInFlight) return;
    if (!opts.force && state.waitTrendLoaded) return;

    state.waitTrendInFlight = true;

    try {
        const url = '/api/live/wait-trend?instance=' + encodeURIComponent(state.instance || '') + '&hours=6&top=5';
        const data = await getJson(url);
        renderWaitTrend(data);
        state.waitTrendLoaded = true;
    } catch {
        // Sessizce boş kalır - bu, saniyelik canlı Bekleme kartının
        // altındaki isteğe bağlı bir ek, ana kartı hataya boğmasın.
        $('waitTrendBody').innerHTML = '';
    } finally {
        state.waitTrendInFlight = false;
    }
}

function renderWaitTrend(data) {
    const series = (data && data.series) || [];

    $('waitTrendBody').innerHTML = series.length === 0
        ? '<p class="empty">Henüz yeterli geçmiş yok.</p>'
        : `<div class="wait-trend-list">${series.map(s => `
            <div class="wait-trend-item">
                <span class="wait-trend-label" title="${esc(s.waitType)}">${esc(s.waitType)}</span>
                <div class="wait-trend-graph">${sparkline(s.values, '#d1477e')}</div>
            </div>`).join('')}</div>`;
}

function renderBlocking(b) {
    badge('blockingBadge', b.severity);

    if (!b.chains || b.chains.length === 0) {
        $('blockingBody').innerHTML = '<p class="empty">Bloklanan oturum yok.</p>';
        return;
    }

    const head = b.headBlockerSessionId
        ? `<p class="blocking-head-note">Baş engelleyici: <b>SPID ${b.headBlockerSessionId}</b> —
           zinciri çözmek için önce buna bak.
           <button type="button" class="btn-kill" data-kill="${b.headBlockerSessionId}">Sonlandır</button></p>`
        : '';

    const rows = b.chains.map(c => `
        <tr>
            <td class="num">${c.blockedSessionId}</td>
            <td class="num">${c.blockingSessionId}<button type="button" class="btn-kill-inline"
                data-kill="${c.blockingSessionId}" title="SPID ${c.blockingSessionId}'i sonlandır">✕</button></td>
            <td>${esc(c.databaseName)}</td>
            <td>${esc(c.waitType)}</td>
            <td class="num">${duration(c.waitTimeMs)}</td>
            <td class="sql" title="${esc(c.blockedSql)}">${esc(c.blockedSql)}</td>
        </tr>`).join('');

    $('blockingBody').innerHTML = head + `
        <div class="table-wrap">
            <table class="grid">
                <thead><tr>
                    <th class="num">Bloklanan</th><th class="num">Blokçu</th>
                    <th>Veritabanı</th><th>Bekleme</th>
                    <th class="num">Süre</th><th>Sorgu</th>
                </tr></thead>
                <tbody>${rows}</tbody>
            </table>
        </div>`;

    $('blockingBody').querySelectorAll('[data-kill]').forEach(btn =>
        btn.addEventListener('click', () => handleKillSession(Number(btn.dataset.kill))));
}

/**
 * Bir oturumu sonlandırır (KILL) - geri alınamaz bir eylem, o yüzden
 * onay isteniyor. Bloklama zincirindeki baş engelleyici ya da herhangi
 * bir satırdaki blokçu için kullanılıyor (bkz. renderBlocking).
 */
async function handleKillSession(sessionId) {
    if (!window.confirm(
        `SPID ${sessionId} sonlandırılsın mı?\n\n` +
        `Bu işlem geri alınamaz - o oturumdaki kaydedilmemiş her şey rollback edilir.`
    )) return;

    try {
        const res = await fetch('/api/live/kill-session', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ instance: state.instance, sessionId })
        });
        const data = await res.json().catch(() => ({}));

        if (!res.ok) {
            window.alert(data.error || data.detail || ('Sonlandırılamadı: HTTP ' + res.status));
            return;
        }

        await refresh();
    } catch (ex) {
        window.alert('Sonlandırılamadı: ' + ex.message);
    }
}

function renderRequests(activity) {
    const tbody = $('requestsTable').querySelector('tbody');
    const rows = activity.requests || [];

    if (rows.length === 0) {
        tbody.innerHTML = '<tr><td colspan="9" class="empty">Çalışan kullanıcı isteği yok.</td></tr>';
        return;
    }

    tbody.innerHTML = rows.map(r => {
        // Uzun süren istekleri renklendir; gözün takılması gereken yer orası.
        const sev = r.elapsedMs > 300000 ? 'sev-critical'
                  : r.elapsedMs > 30000  ? 'sev-warning' : '';

        return `
        <tr class="clickable" data-session="${r.sessionId}">
            <td class="num">${r.sessionId}</td>
            <td>${esc(r.databaseName)}</td>
            <td>${esc(r.status)}</td>
            <td>${esc(r.waitType || '—')}</td>
            <td class="num ${sev}">${duration(r.elapsedMs)}</td>
            <td class="num">${duration(r.cpuMs)}</td>
            <td class="num">${num(r.logicalReads)}</td>
            <td class="num">${r.blockedBy || '—'}</td>
            <td class="sql" title="${esc(r.sqlText)}">${esc(r.sqlText)}</td>
        </tr>`;
    }).join('');
}

function renderTimeline(entries) {
    if (!entries || entries.length === 0) {
        $('timeline').innerHTML =
            '<li class="empty">Henüz kayıtlı durum değişikliği yok. Toplayıcı çalıştıkça dolacak.</li>';
        return;
    }

    // Çizelge her yenilemede baştan çiziliyor; açık paneller kapanmasın
    // diye hangilerinin açık olduğu önce not ediliyor, çizimden sonra
    // aynıları (önbellekten, anında) geri açılıyor.
    const open = new Set(
        Array.from($('timeline').querySelectorAll('.tl-item.is-open'))
             .map(li => li.dataset.event));

    $('timeline').innerHTML = entries.map(e => `
        <li class="tl-item ${sevClass(e.severity)}${e.hasDetail ? ' tl-clickable' : ''}"
            ${e.hasDetail ? `data-event="${e.eventId}"` : ''}>
            <span class="tl-time">${dayTime(e.occurredAtUtc)}</span>
            <div>
                <p class="tl-title">${esc(e.title)} — ${esc(sevLabel(e.severity))}${
                    e.hasDetail ? '<span class="tl-more">ayrıntı</span>' : ''}</p>
                ${e.detail ? `<p class="tl-detail">${esc(e.detail)}</p>` : ''}
                ${e.hasDetail ? '<div class="tl-evidence" hidden></div>' : ''}
            </div>
        </li>`).join('');

    $('timeline').querySelectorAll('.tl-clickable').forEach(li => {
        li.addEventListener('click', () => toggleEventDetail(li));

        // Panelin İÇİNE tıklamak satırı kapatmasın - kullanıcı oradaki
        // sorgu metnini seçip kopyalayacak, ilk sürüklemede kapanırsa
        // panel kullanılamaz hâle gelir.
        li.querySelector('.tl-evidence')
          ?.addEventListener('click', (ev) => ev.stopPropagation());

        if (open.has(li.dataset.event)) showEventDetail(li);
    });
}

function toggleEventDetail(li) {
    const box = li.querySelector('.tl-evidence');
    if (!box) return;

    if (!box.hidden) {              // açıksa kapat
        box.hidden = true;
        li.classList.remove('is-open');
        return;
    }
    showEventDetail(li);
}

/** Kanıt BİR KEZ çekilir ve state.eventEvidence'ta tutulur. Geçmiş bir
    olayın kanıtı tanımı gereği değişmez; ayrıca çizelge 30 saniyede bir
    yeniden çiziliyor - önbellek olmasaydı açık bir panel her yenilemede
    "Yükleniyor…"a düşerdi. */
async function showEventDetail(li) {
    const box = li.querySelector('.tl-evidence');
    const id = li.dataset.event;

    box.hidden = false;
    li.classList.add('is-open');

    const cached = state.eventEvidence[id];
    if (cached) { renderEventEvidence(box, cached); return; }

    box.innerHTML = '<p class="tl-ev-note">Yükleniyor…</p>';
    try {
        const url = `/api/live/event/${id}/detail?instance=` +
                    encodeURIComponent(state.instance || '');
        const rows = await getJson(url);
        state.eventEvidence[id] = rows;
        renderEventEvidence(box, rows);
    } catch (err) {
        // Hata ÖNBELLEĞE GİRMİYOR: geçici bir ağ hatasından sonra
        // kullanıcı tekrar tıkladığında yeniden denensin.
        box.innerHTML = `<p class="tl-ev-note is-error">Alınamadı: ${esc(err.message)}</p>`;
    }
}

function renderEventEvidence(box, rows) {
    if (!rows || rows.length === 0) {
        box.innerHTML =
            '<p class="tl-ev-note">Bu olay için kayıtlı sorgu bulunamadı.</p>';
        return;
    }

    box.innerHTML = rows.map(r => {
        // Ad-hoc bir sorguda prosedür adı yoktur; "" yerine ne olduğunu
        // söylemek, boş bir başlıktan iyidir.
        const name = r.objectName
            ? `<span class="tl-ev-obj">${esc(r.objectName)}</span>`
            : '<span class="tl-ev-obj is-adhoc">ad-hoc sorgu</span>';

        const meta = [
            r.databaseName && `veritabanı: ${r.databaseName}`,
            r.loginName && `kullanıcı: ${r.loginName}`,
            r.hostName && `makine: ${r.hostName}`,
            r.programName && `uygulama: ${r.programName}`,
            r.waitType && `bekleme: ${r.waitType}`,
            r.blockedBy > 0 && `SPID ${r.blockedBy} tarafından bloklanmış`
        ].filter(Boolean).map(esc).join(' · ');

        return `
            <div class="tl-ev">
                <p class="tl-ev-head">
                    ${name}
                    <span class="tl-ev-dur">${duration(r.elapsedSeconds * 1000)}</span>
                    <span class="tl-ev-spid">SPID ${r.sessionId}</span>
                </p>
                <p class="tl-ev-meta">${meta}</p>
                <pre class="tl-ev-sql">${r.sqlText
                    ? highlightSql(r.sqlText)
                    : '(sorgu metni alınamadı)'}</pre>
            </div>`;
    }).join('');
}

/** Ortalama süre/CPU sütunları için: duration() saat/dakikaya yuvarlar, buradaki
    değerler çoğu zaman tek haneli milisaniyedir - "0 sn" göstermek işe yaramaz.
    Stored Procedure paneli kullanıyor - En Yoğun Sorgular'dan kalan, paylaşılan bir yardımcı. */
function ms1(v) {
    return num(v, v < 10 ? 2 : 1) + ' ms';
}

// ------------------------------------------------------------------
// Index Analizi (eksik index önerileri)
// ------------------------------------------------------------------

function objLabelMi(r) {
    return `<span class="mi-obj">${esc(r.databaseName)}.${esc(r.schemaName)}.${esc(r.tableName)}</span>`;
}

/** .tq-bar'ın altın renkli eşdeğeri - iki bileşen kimlik rengi paylaşmasın diye ayrı. */
function miShareBar(pct, max) {
    const ratio = max > 0 ? (pct / max) * 100 : 0;
    const w = Math.max(pct > 0 ? 4 : 0, Math.min(100, ratio));
    return `<span class="mi-bar" title="listedeki en yüksek etkiye göre"><i style="width:${w}%"></i></span>`;
}

/** last_user_seek/last_user_scan NULL olabilir - hiç seek olmadan yalnızca scan görülmüş olabilir. */
function miLastSeen(iso) {
    return iso ? dayTime(iso) : '—';
}

function renderMiStats(data) {
    const rows = data.rows || [];
    const totalSeeks = rows.reduce((sum, r) => sum + r.seeks, 0);
    const uptimeHours = state.lastSnapshot ? state.lastSnapshot.server.uptimeHours : null;

    $('miStats').innerHTML = `
        <div class="mi-stat">
            <p class="mi-stat-label">Öneri</p>
            <p class="mi-stat-value">${num(rows.length)}</p>
            <p class="mi-stat-note">sunucu genelinde</p>
        </div>
        <div class="mi-stat">
            <p class="mi-stat-label">Toplam seek</p>
            <p class="mi-stat-value">${num(totalSeeks)}</p>
            <p class="mi-stat-note">bu index'ler var olsaydı kullanılacak sorgu sayısı</p>
        </div>
        <div class="mi-stat">
            <p class="mi-stat-label">Kapsanan pencere</p>
            <p class="mi-stat-value">${uptimeHours != null ? num(uptimeHours, 0) + ' sa' : '—'}</p>
            <p class="mi-stat-note">açılıştan beri - yeniden başlatmada sıfırlanır</p>
        </div>`;
}

function renderMiHero(r) {
    return `
        <div class="mi-hero">
            <div class="mi-hero-head">
                <span class="mi-hero-rank">1</span>
                <div>
                    <p class="mi-hero-label">En yüksek etkili öneri</p>
                    ${objLabelMi(r)}
                </div>
            </div>
            <pre class="mi-hero-sql">${highlightSql(r.suggestedCreateIndex || '(anlamlı bir öneri çıkmadı)')}</pre>
            <div class="mi-hero-facts">
                <div><p class="mi-fact-label">Etki</p><p class="mi-fact-value">${num(r.impact, 0)}</p></div>
                <div><p class="mi-fact-label">Seek</p><p class="mi-fact-value">${num(r.seeks)}</p></div>
                <div><p class="mi-fact-label">Scan</p><p class="mi-fact-value">${num(r.scans)}</p></div>
                <div><p class="mi-fact-label">Ort. sorgu maliyeti</p><p class="mi-fact-value">${num(r.avgTotalUserCost, 1)}</p></div>
                <div><p class="mi-fact-label">Ort. etki</p><p class="mi-fact-value">%${num(r.avgUserImpact, 1)}</p></div>
            </div>
            <div class="mi-hero-foot">
                <p class="mi-hero-seen">Son seek ${miLastSeen(r.lastUserSeek)}</p>
                <button type="button" class="btn btn-index" id="miHeroInspect">İncele</button>
            </div>
        </div>`;
}

function renderMissingIndexes(data) {
    const rows = data.rows || [];
    renderMiStats(data);

    const tbody = $('miTable').querySelector('tbody');

    if (rows.length === 0) {
        $('miHero').innerHTML = '';
        tbody.innerHTML = '<tr><td colspan="7" class="empty">Şu an bekleyen bir eksik index önerisi yok.</td></tr>';
        return;
    }

    const [top, ...rest] = rows;
    const maxImpact = Math.max(1, ...rows.map(r => r.impact));

    $('miHero').innerHTML = renderMiHero(top);
    $('miHeroInspect').addEventListener('click', () => openMissingIndexDrawer(top));

    if (rest.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" class="empty">Listede başka öneri yok.</td></tr>';
        return;
    }

    tbody.innerHTML = rest.map((r, i) => {
        const cols = [r.equalityColumns, r.inequalityColumns].filter(Boolean).join(', ') +
                     (r.includeColumns ? ` +${r.includeColumns}` : '');
        return `
        <tr class="clickable" data-idx="${i}">
            <td class="num">${i + 2}</td>
            <td>${objLabelMi(r)}</td>
            <td class="sql" title="${esc(cols)}">${esc(cols)}</td>
            <td class="num">${num(r.impact, 0)}</td>
            <td class="num">${num(r.seeks)}</td>
            <td class="num"><div class="mi-table-pct">${miShareBar(r.impact, maxImpact)}<span>%${num(r.avgUserImpact, 0)}</span></div></td>
            <td class="num">${miLastSeen(r.lastUserSeek)}</td>
        </tr>`;
    }).join('');

    tbody.querySelectorAll('tr[data-idx]').forEach(tr => {
        tr.addEventListener('click', () => openMissingIndexDrawer(rest[Number(tr.dataset.idx)]));
    });
}

async function loadMissingIndexes(opts = {}) {
    if (state.miInFlight) return;
    if (!opts.force && state.miLoaded) return;

    state.miInFlight = true;

    const status = $('miStatus');
    status.hidden = false;
    status.classList.remove('is-error');
    status.textContent = 'Yükleniyor…';

    try {
        const url = '/api/live/missing-indexes?instance=' + encodeURIComponent(state.instance || '') + '&top=100';
        const data = await getJson(url);

        renderMissingIndexes(data);
        state.miLoaded = true;
        status.hidden = true;
    } catch (err) {
        status.hidden = false;
        status.classList.add('is-error');
        status.textContent = 'Alınamadı: ' + err.message;
    } finally {
        state.miInFlight = false;
    }
}

/** Bir eksik index satırının CREATE script'i + DMV istatistikleri - plan yok, "Yürütme planı" bölümü gizleniyor. */
function openMissingIndexDrawer(r) {
    $('drawer').hidden = false;
    $('drawerTitle').textContent = `${r.databaseName}.${r.schemaName}.${r.tableName}`;
    setDrawerSql(r.suggestedCreateIndex || '(anlamlı bir öneri çıkmadı)');
    $('drawerPlanSection').hidden = true;

    $('drawerStatBox').hidden = false;
    $('drawerStatBox').innerHTML = `
        <dt>Etki</dt><dd>${num(r.impact, 0)}</dd>
        <dt>Ort. etki</dt><dd>%${num(r.avgUserImpact, 1)}</dd>
        <dt>Seek</dt><dd>${num(r.seeks)}</dd>
        <dt>Scan</dt><dd>${num(r.scans)}</dd>
        <dt>Ort. sorgu maliyeti</dt><dd>${num(r.avgTotalUserCost, 2)}</dd>
        <dt>Benzersiz derleme</dt><dd>${num(r.uniqueCompiles)}</dd>
        <dt>Son seek</dt><dd>${miLastSeen(r.lastUserSeek)}</dd>
        <dt>Son scan</dt><dd>${miLastSeen(r.lastUserScan)}</dd>`;
}

// ------------------------------------------------------------------
// Stored Procedure (sys.dm_exec_procedure_stats)
//
// En Yoğun Sorgular'la aynı iskelet, tek fark: satırlarda hazır SQL
// metni YOK (prosedür tanımı listelemede çekilmiyor, pahalı olurdu).
// Bu yüzden hero'da metin önizlemesi yok, ve çekmece SQL'i "İncele"ye
// basılınca ayrı bir istekle (openProcedureDrawer) geliyor.
// ------------------------------------------------------------------

function objLabelSp(r) {
    return `<span class="sp-obj">${esc(r.databaseName)}.${esc(r.schemaName)}.${esc(r.procName)}</span>`;
}

/** .tq-bar/.mi-bar'ın indigo eşdeğeri. */
function spShareBar(pct, max) {
    const ratio = max > 0 ? (pct / max) * 100 : 0;
    const w = Math.max(pct > 0 ? 4 : 0, Math.min(100, ratio));
    return `<span class="sp-bar" title="listedeki en yüksek paya göre"><i style="width:${w}%"></i></span>`;
}

function renderSpStats(data) {
    const rows = data.rows || [];
    $('spStats').innerHTML = `
        <div class="sp-stat">
            <p class="sp-stat-label">Prosedür</p>
            <p class="sp-stat-value">${num(rows.length)}</p>
            <p class="sp-stat-note">plan cache'teki (top liste)</p>
        </div>
        <div class="sp-stat">
            <p class="sp-stat-label">Toplam çağrı</p>
            <p class="sp-stat-value">${num(data.totalCallsAllCache)}</p>
            <p class="sp-stat-note">cache'e girişten beri · ${num(data.cachedProcCount)} prosedür</p>
        </div>
        <div class="sp-stat">
            <p class="sp-stat-label">Toplam CPU</p>
            <p class="sp-stat-value">${duration(data.listedTotalCpuMs)}</p>
            <p class="sp-stat-note">listelenen prosedürlerin toplamı</p>
        </div>
        <div class="sp-stat">
            <p class="sp-stat-label">Toplam süre</p>
            <p class="sp-stat-value">${duration(data.listedTotalDurationMs)}</p>
            <p class="sp-stat-note">listelenen prosedürlerin toplamı</p>
        </div>`;
}

function renderSpHero(r, maxShare) {
    return `
        <div class="sp-hero">
            <div class="sp-hero-head">
                <span class="sp-hero-rank">1</span>
                <div>
                    <p class="sp-hero-label">En ağır prosedür</p>
                    ${objLabelSp(r)}
                </div>
            </div>
            <div class="sp-hero-facts">
                <div><p class="sp-fact-label">Çağrı</p><p class="sp-fact-value">${num(r.calls)}</p></div>
                <div><p class="sp-fact-label">Toplam CPU</p><p class="sp-fact-value">${duration(r.totalCpuMs)}</p></div>
                <div><p class="sp-fact-label">Ort. CPU</p><p class="sp-fact-value">${ms1(r.avgCpuMs)}</p></div>
                <div><p class="sp-fact-label">Toplam süre</p><p class="sp-fact-value">${duration(r.totalDurationMs)}</p></div>
                <div><p class="sp-fact-label">Ort. süre</p><p class="sp-fact-value">${ms1(r.avgDurationMs)}</p></div>
                <div><p class="sp-fact-label">Mant. okuma</p><p class="sp-fact-value">${num(r.logicalReads)} <span class="kpi-unit">(${pagesToSize(r.logicalReads)})</span></p></div>
                <div><p class="sp-fact-label">Mant. yazma</p><p class="sp-fact-value">${num(r.logicalWrites)}</p></div>
                <div>
                    <p class="sp-fact-label">Pay</p>
                    <div class="sp-fact-pay">
                        <p class="sp-fact-value">%${num(r.durationSharePercent, 1)}</p>
                        ${spShareBar(r.durationSharePercent, maxShare)}
                    </div>
                </div>
            </div>
            <div class="sp-hero-foot">
                <p class="sp-hero-seen">Cache'e girdi ${dayTime(r.cachedTime)} → son çalışma ${dayTime(r.lastExecutionTime)}</p>
                <button type="button" class="btn btn-proc" id="spHeroInspect">İncele</button>
            </div>
        </div>`;
}

/** Sütun başlığındaki ok işaretini sunucunun DOĞRULADIĞI sort/yöne göre günceller -
    istemci tahminine değil, data.sortKey/sortDescending'e güveniyor. */
function markSortedHeader(tableId, sortKey, descending) {
    document.querySelectorAll(`#${tableId} thead th.sortable-th`).forEach(th => {
        th.classList.remove('is-sorted-asc', 'is-sorted-desc');
        if (th.dataset.sort === sortKey)
            th.classList.add(descending ? 'is-sorted-desc' : 'is-sorted-asc');
    });
}

function renderTopProcedures(data) {
    const rows = data.rows || [];
    renderSpStats(data);
    markSortedHeader('spTable', data.sortKey, data.sortDescending);

    const tbody = $('spTable').querySelector('tbody');

    if (rows.length === 0) {
        $('spHero').innerHTML = '';
        tbody.innerHTML = '<tr><td colspan="14" class="empty">Plan cache\'te prosedür bulunamadı.</td></tr>';
        return;
    }

    const [top, ...rest] = rows;
    const maxShare = Math.max(1, ...rows.map(r => r.durationSharePercent));

    $('spHero').innerHTML = renderSpHero(top, maxShare);
    $('spHeroInspect').addEventListener('click', () => openProcedureDrawer(top));

    if (rest.length === 0) {
        tbody.innerHTML = '<tr><td colspan="14" class="empty">Listede başka prosedür yok.</td></tr>';
        return;
    }

    tbody.innerHTML = rest.map((r, i) => `
        <tr class="clickable" data-idx="${i}">
            <td class="num">${i + 2}</td>
            <td>${objLabelSp(r)}</td>
            <td class="num">${num(r.calls)}</td>
            <td class="num">${duration(r.totalCpuMs)}</td>
            <td class="num">${ms1(r.avgCpuMs)}</td>
            <td class="num">${ms1(r.avgDurationMs)}</td>
            <td class="num">${num(r.logicalReads)}</td>
            <td class="num">${pagesToSize(r.logicalReads)}</td>
            <td class="num">${num(r.avgLogicalReads, 1)}</td>
            <td class="num">${pagesToMB(r.avgLogicalReads)}</td>
            <td class="num">${num(r.logicalWrites)}</td>
            <td class="num">${num(r.avgLogicalWrites, 1)}</td>
            <td class="num"><div class="sp-table-pct">${spShareBar(r.durationSharePercent, maxShare)}<span>%${num(r.durationSharePercent, 1)}</span></div></td>
            <td class="num">${dayTime(r.lastExecutionTime)}</td>
        </tr>`).join('');

    tbody.querySelectorAll('tr[data-idx]').forEach(tr => {
        tr.addEventListener('click', () => openProcedureDrawer(rest[Number(tr.dataset.idx)]));
    });
}

/**
 * @param {string} dir 'desc' ya da 'asc' - sıralama hap grubu her zaman
 * 'desc' ile çağırır (o metriğin "en kötüsü" mantığı), sütun başlığına
 * tıklamak mevcut yönü tersine çevirir - bkz. #spTable başlık tıklama.
 */
async function loadTopProcedures(sort, dir, opts = {}) {
    if (state.spInFlight) return;
    if (!opts.force && state.spLoaded && sort === state.spSort && dir === state.spDir) return;

    state.spSort = sort;
    state.spDir = dir;
    state.spInFlight = true;

    document.querySelectorAll('#spSort button').forEach(b =>
        b.classList.toggle('is-active', b.dataset.sort === sort && dir === 'desc'));

    const status = $('spStatus');
    status.hidden = false;
    status.classList.remove('is-error');
    status.textContent = 'Yükleniyor…';

    try {
        const url = '/api/live/top-procedures?instance=' + encodeURIComponent(state.instance || '') +
                    '&sort=' + encodeURIComponent(sort) + '&dir=' + encodeURIComponent(dir) + '&top=50';
        const data = await getJson(url);

        renderTopProcedures(data);
        state.spLoaded = true;
        status.hidden = true;
    } catch (err) {
        status.hidden = false;
        status.classList.add('is-error');
        status.textContent = 'Alınamadı: ' + err.message;
    } finally {
        state.spInFlight = false;
    }
}

/**
 * Bir prosedür satırının tam tanımı + DMV istatistikleri.
 *
 * ASENKRON olmak zorunda: openMissingIndexDrawer'ın aksine, satırda
 * hazır SQL metni yok - tanım yalnızca burada, tıklanınca, sql_handle
 * üzerinden ayrı bir istekle çekiliyor (sunucu-çapında handle mekanizması,
 * bkz. StoredProcedureService.GetDefinitionAsync).
 */
async function openProcedureDrawer(r) {
    openDrawerShell(`${r.databaseName}.${r.schemaName}.${r.procName}`);
    $('drawerPlanSection').hidden = true;

    $('drawerStatBox').hidden = false;
    $('drawerStatBox').innerHTML = `
        <dt>Çağrı</dt><dd>${num(r.calls)}</dd>
        <dt>Toplam CPU</dt><dd>${duration(r.totalCpuMs)}</dd>
        <dt>Ort. CPU</dt><dd>${ms1(r.avgCpuMs)}</dd>
        <dt>Toplam süre</dt><dd>${duration(r.totalDurationMs)}</dd>
        <dt>Ort. süre</dt><dd>${ms1(r.avgDurationMs)}</dd>
        <dt>Mant. okuma</dt><dd>${num(r.logicalReads)} (${pagesToSize(r.logicalReads)})</dd>
        <dt>Mant. yazma</dt><dd>${num(r.logicalWrites)}</dd>
        <dt>Cache'e giriş</dt><dd>${dayTime(r.cachedTime)}</dd>
        <dt>Son çalışma</dt><dd>${dayTime(r.lastExecutionTime)}</dd>`;

    try {
        const url = '/api/live/procedure-definition?instance=' + encodeURIComponent(state.instance || '') +
                    '&sqlHandle=' + encodeURIComponent(r.sqlHandle) +
                    '&planHandle=' + encodeURIComponent(r.planHandle);
        const data = await getJson(url);

        setDrawerSql(data.definition);
    } catch (err) {
        $('drawerSql').textContent = err.message.includes('404')
            ? 'Bu prosedürün tanımı artık cache\'te değil — tahliye olmuş olabilir (yeniden başlatma, recompile, bellek baskısı).'
            : 'Alınamadı: ' + err.message;
    }
}

// ------------------------------------------------------------------
// Kullanılmayan Prosedürler
//
// "Listede olmak" = "kullanılmıyor" DEĞİL, "bu gözlem penceresinde
// çalıştığına dair kanıt bulunamadı" demektir (bkz. DmvSql.UnusedProceduresScan
// baştaki uzun not). Bu yüzden her veritabanı notu kanıtın gücünü açıkça
// söylüyor - sessizce tek bir listeymiş gibi sunmuyoruz.
// ------------------------------------------------------------------

function objLabelUnused(r) {
    return `<span class="sp-obj">${esc(r.databaseName)}.${esc(r.schemaName)}.${esc(r.procName)}</span>`;
}

/**
 * tier: 2=güçlü (pencerenin TAMAMI boyunca hiç görülmedi), 1=zayıf-yeni
 * (Query Store açık ama proc pencereden daha yeni - henüz yeterince
 * gözlemlenmedi), 0=zayıf (Query Store hiç açık değil).
 */
function evidenceBadge(tier) {
    if (tier === 2) return '<span class="evidence-badge is-strong">Güçlü — tüm pencere boyunca görülmedi</span>';
    if (tier === 1) return '<span class="evidence-badge is-partial">Zayıf — proc pencereden yeni, henüz kanıtsız</span>';
    return '<span class="evidence-badge is-weak">Zayıf — Query Store kapalı</span>';
}

function renderUnusedDbNotes(databases) {
    const uptimeHours = state.lastSnapshot ? state.lastSnapshot.server.uptimeHours : null;

    $('unusedDbNotes').innerHTML = (databases || []).map(d => {
        const strong = d.queryStoreEnabled;
        const qsDays = (strong && d.qsEarliest && d.qsLatest)
            ? Math.max(0, Math.round((new Date(d.qsLatest) - new Date(d.qsEarliest)) / 86400000))
            : null;
        // Query Store az önce açıldıysa (pencere birkaç günden kısa) "hiç
        // görülmedi" demek neredeyse hiçbir şey ispatlamaz - kullanıcı bunu
        // fark etmeden temizlik yapmasın diye açıkça uyarıyoruz.
        const windowTooShort = strong && qsDays !== null && qsDays < 7;

        const evidenceText = !strong
            ? `Query Store kapalı — yalnızca plan cache'e bakılabildi (zayıf kanıt). Sunucu ` +
              `${uptimeHours != null ? num(uptimeHours, 0) + ' saattir' : 'bir süredir'} açık, ama önbellek ` +
              `bundan çok daha sık boşalabilir (recompile, bellek baskısı).`
            : windowTooShort
                ? `Query Store yeni açıldı — yalnızca ${num(qsDays, 0)} günlük veri var ` +
                  `(${dayTime(d.qsEarliest)}'dan beri). Bu kadar kısa bir pencerede "görülmedi" demek çok az şey ` +
                  `kanıtlar — güvenilir bir liste için birkaç hafta beklemenizi öneririm.`
                : `Query Store açık — son ${num(qsDays, 0)} gün kontrol edildi ` +
                  `(${dayTime(d.qsEarliest)} → ${dayTime(d.qsLatest)}), ${num(d.qsStaleThresholdDays, 0)} günden ` +
                  `eski geçmişi kendi siliyor. Aşağıdaki "Güçlü" rozetli satırlar bu pencerenin TAMAMI boyunca hiç ` +
                  `görülmedi; "Zayıf — yeni" rozetliler pencereden daha yakın zamanda oluşturulmuş, henüz karar vermek için erken.`;

        const evidenceClass = !strong ? 'is-weak' : windowTooShort ? 'is-partial' : 'is-strong';

        return `
            <div class="unused-dbnote">
                <div class="unused-dbnote-head">
                    <span class="unused-dbnote-name">${esc(d.databaseName)}</span>
                    <span class="unused-dbnote-count">${num(d.unseenProcs)} / ${num(d.totalProcs)}</span>
                </div>
                <div class="unused-dbnote-evidence ${evidenceClass}">${evidenceText}</div>
            </div>`;
    }).join('') || '<p class="empty">Taranacak kullanıcı veritabanı bulunamadı.</p>';
}

function renderUnusedProcedures(data) {
    renderUnusedDbNotes(data.databases);

    const strongOnly = $('unusedStrongOnly').checked;
    const rows = (data.rows || []).filter(r => !strongOnly || r.evidenceTier === 2);
    const tbody = $('unusedTable').querySelector('tbody');

    if (rows.length === 0) {
        tbody.innerHTML = `<tr><td colspan="5" class="empty">${
            strongOnly
                ? 'Güçlü kanıtlı (gözlem penceresinin tamamında hiç görülmemiş) prosedür yok.'
                : 'Taranan veritabanlarında kanıtsız (kullanılmayan görünen) prosedür yok.'
        }</td></tr>`;
        return;
    }

    tbody.innerHTML = rows.map((r, i) => `
        <tr class="clickable" data-idx="${i}">
            <td class="num">${i + 1}</td>
            <td>${objLabelUnused(r)}</td>
            <td class="num">${dayTime(r.createDate)}</td>
            <td class="num">${dayTime(r.modifyDate)}</td>
            <td>${evidenceBadge(r.evidenceTier)}</td>
        </tr>`).join('');

    tbody.querySelectorAll('tr[data-idx]').forEach(tr => {
        tr.addEventListener('click', () => openUnusedProcedureDrawer(rows[Number(tr.dataset.idx)]));
    });
}

async function loadUnusedProcedures(opts = {}) {
    if (state.unusedInFlight) return;
    if (!opts.force && state.unusedLoaded) return;

    state.unusedInFlight = true;

    const status = $('unusedStatus');
    status.hidden = false;
    status.classList.remove('is-error');
    status.textContent = 'Taranıyor… (birden fazla veritabanı kontrol ediliyor)';

    try {
        const url = '/api/live/unused-procedures?instance=' + encodeURIComponent(state.instance || '') + '&top=150';
        const data = await getJson(url);

        state.unusedData = data;
        renderUnusedProcedures(data);
        state.unusedLoaded = true;
        status.hidden = true;
    } catch (err) {
        status.hidden = false;
        status.classList.add('is-error');
        status.textContent = 'Alınamadı: ' + err.message;
    } finally {
        state.unusedInFlight = false;
    }
}

/** sql_handle'ı OLMAYAN bir prosedürün tanımı - openProcedureDrawer'daki
    plan-cache yolundan FARKLI bir uçtan (procedure-source) gelir. */
async function openUnusedProcedureDrawer(r) {
    openDrawerShell(`${r.databaseName}.${r.schemaName}.${r.procName}`);
    $('drawerPlanSection').hidden = true;

    $('drawerStatBox').hidden = false;
    $('drawerStatBox').innerHTML = `
        <dt>Oluşturulma</dt><dd>${dayTime(r.createDate)}</dd>
        <dt>Son değişiklik</dt><dd>${dayTime(r.modifyDate)}</dd>
        <dt>Kanıt</dt><dd>${r.queryStoreEnabled ? 'Plan cache + Query Store' : 'Yalnızca plan cache'}</dd>`;

    try {
        const url = '/api/live/procedure-source?instance=' + encodeURIComponent(state.instance || '') +
                    '&database=' + encodeURIComponent(r.databaseName) +
                    '&schema=' + encodeURIComponent(r.schemaName) +
                    '&proc=' + encodeURIComponent(r.procName);
        const data = await getJson(url);

        setDrawerSql(data.definition);
    } catch (err) {
        $('drawerSql').textContent = err.message.includes('404')
            ? 'Bu prosedür artık bulunamıyor — tarama ile tıklama arasında silinmiş olabilir.'
            : 'Alınamadı: ' + err.message;
    }
}

// ------------------------------------------------------------------
// Always On
// ------------------------------------------------------------------

/**
 * SQL Server'ın _desc sözcüklerini (HEALTHY, CONNECTED, SYNCHRONIZED,
 * NORMAL_QUORUM, UP, ...) ortak bir iyi/orta/kötü ölçeğine eşler.
 * goodValues/warnValues büyük harf bekler - geri kalan her şey "kötü"
 * sayılır (AG'de "bilinmiyor" durumu da pratikte dikkat gerektirir,
 * sessizce nötr göstermek yanlış güven verir).
 */
function statusPill(text, goodValues, warnValues) {
    if (!text) return '<span class="status-pill is-neutral">—</span>';
    const upper = text.toUpperCase();
    const cls = goodValues.includes(upper) ? 'is-good'
        : warnValues.includes(upper) ? 'is-warn'
        : 'is-bad';
    return `<span class="status-pill ${cls}">${esc(text)}</span>`;
}

function kbFmt(kb) {
    if (kb == null) return '—';
    if (kb === 0) return '<span class="ag-db-lag is-zero">0 KB</span>';
    return `<span class="ag-db-lag">${num(kb)} KB</span>`;
}

function renderAgSummary(data) {
    const g = (data.groups && data.groups[0]) || {};
    const cluster = data.cluster || {};

    $('agSummary').innerHTML = `
        <div class="ag-summary-item">
            <p class="ag-summary-label">AG grubu</p>
            <p class="ag-summary-value">${esc(g.groupName || '—')}</p>
        </div>
        <div class="ag-summary-item">
            <p class="ag-summary-label">Primary</p>
            <p class="ag-summary-value">${esc(g.primaryReplica || '—')}</p>
        </div>
        <div class="ag-summary-item">
            <p class="ag-summary-label">Senkron sağlığı</p>
            <p class="ag-summary-value">${statusPill(g.synchronizationHealth, ['HEALTHY'], ['PARTIALLY_HEALTHY'])}</p>
        </div>
        <div class="ag-summary-item">
            <p class="ag-summary-label">Cluster / quorum</p>
            <p class="ag-summary-value">${cluster.clusterName ? esc(cluster.clusterName) + ' ' : ''}${
                cluster.quorumState ? statusPill(cluster.quorumState, ['NORMAL_QUORUM'], ['FORCE_QUORUM']) : '—'}</p>
        </div>`;
}

function renderAgReplicas(data) {
    $('agReplicas').innerHTML = (data.replicas || []).map(r => `
        <div class="ag-replica-card ${r.roleDesc === 'PRIMARY' ? 'is-primary' : ''}">
            <div class="ag-replica-head">
                <div>
                    <p class="ag-replica-name">${esc(r.replicaServerName)}</p>
                    <p class="ag-replica-role">${esc(r.roleDesc || 'BİLİNMİYOR')}</p>
                </div>
                ${statusPill(r.connectedState, ['CONNECTED'], [])}
            </div>
            <div class="ag-replica-facts">
                <div><p class="ag-fact-label">Sağlık</p><p class="ag-fact-value">${statusPill(r.synchronizationHealth, ['HEALTHY'], ['PARTIALLY_HEALTHY'])}</p></div>
                <div><p class="ag-fact-label">Commit modu</p><p class="ag-fact-value">${esc(r.availabilityMode || '—')}</p></div>
                <div><p class="ag-fact-label">Failover</p><p class="ag-fact-value">${esc(r.failoverMode || '—')}</p></div>
                <div><p class="ag-fact-label">Yedek önceliği</p><p class="ag-fact-value">${num(r.backupPriority)}</p></div>
            </div>
            ${r.lastConnectErrorDescription ? `<div class="ag-replica-error">${esc(r.lastConnectErrorDescription)}</div>` : ''}
        </div>`).join('');
}

function renderAgDatabases(data) {
    const tbody = $('agDbTable').querySelector('tbody');
    const rows = data.databases || [];

    if (rows.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" class="empty">Veritabanı bulunamadı.</td></tr>';
        return;
    }

    tbody.innerHTML = rows.map(r => `
        <tr>
            <td>${esc(r.databaseName)}</td>
            <td>${esc(r.replicaServerName)}</td>
            <td>${statusPill(r.synchronizationState, ['SYNCHRONIZED'], ['SYNCHRONIZING', 'INITIALIZING'])}</td>
            <td>${statusPill(r.synchronizationHealth, ['HEALTHY'], ['PARTIALLY_HEALTHY'])}</td>
            <td class="num">${kbFmt(r.logSendQueueKb)}</td>
        </tr>`).join('');
}

async function loadAlwaysOn(opts = {}) {
    if (state.agInFlight) return;
    if (!opts.force && state.agLoaded) return;

    state.agInFlight = true;

    const status = $('agStatus');
    status.hidden = false;
    status.classList.remove('is-error');
    status.textContent = 'Yükleniyor…';

    try {
        const url = '/api/live/alwayson?instance=' + encodeURIComponent(state.instance || '');
        const data = await getJson(url);

        if (!data.hasAvailabilityGroup) {
            $('agNoGroup').hidden = false;
            $('agContent').hidden = true;
        } else {
            $('agNoGroup').hidden = true;
            $('agContent').hidden = false;
            renderAgSummary(data);
            renderAgReplicas(data);
            renderAgDatabases(data);
        }

        state.agLoaded = true;
        status.hidden = true;
    } catch (err) {
        status.hidden = false;
        status.classList.add('is-error');
        status.textContent = 'Alınamadı: ' + err.message;
    } finally {
        state.agInFlight = false;
    }
}

// ------------------------------------------------------------------
// Agent Jobs
// ------------------------------------------------------------------

/** run_status: 0 Failed, 1 Succeeded, 2 Retry, 3 Canceled, null = hiç çalışmamış. */
function jobStatusPill(outcome, isRunning) {
    if (isRunning) return '<span class="status-pill is-warn">ÇALIŞIYOR</span>';
    if (outcome == null) return '<span class="status-pill is-neutral">Hiç çalışmadı</span>';
    if (outcome === 0) return '<span class="status-pill is-bad">BAŞARISIZ</span>';
    if (outcome === 1) return '<span class="status-pill is-good">BAŞARILI</span>';
    if (outcome === 2) return '<span class="status-pill is-warn">TEKRAR DENENİYOR</span>';
    if (outcome === 3) return '<span class="status-pill is-neutral">İPTAL</span>';
    return '<span class="status-pill is-neutral">—</span>';
}

function jobDuration(sec) {
    if (sec == null) return '—';
    if (sec < 60) return sec + ' sn';
    const m = Math.floor(sec / 60), s = sec % 60;
    if (m < 60) return `${m} dk ${s} sn`;
    const h = Math.floor(m / 60), mm = m % 60;
    return `${h} sa ${mm} dk`;
}

function renderJobsSummary(data) {
    $('jobsSummary').innerHTML = `
        <div class="ag-summary-item">
            <p class="ag-summary-label">Toplam job</p>
            <p class="ag-summary-value">${num(data.jobs.length)}</p>
        </div>
        <div class="ag-summary-item">
            <p class="ag-summary-label">Başarısız</p>
            <p class="ag-summary-value">${data.failedCount > 0
                ? `<span class="status-pill is-bad">${num(data.failedCount)}</span>`
                : num(data.failedCount)}</p>
        </div>
        <div class="ag-summary-item">
            <p class="ag-summary-label">Şu an çalışan</p>
            <p class="ag-summary-value">${num(data.runningCount)}</p>
        </div>
        <div class="ag-summary-item">
            <p class="ag-summary-label">Pasif</p>
            <p class="ag-summary-value">${num(data.disabledCount)}</p>
        </div>`;
}

function renderJobsTable(data) {
    const tbody = $('jobsTable').querySelector('tbody');
    const jobs = data.jobs || [];

    if (jobs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" class="empty">Job bulunamadı.</td></tr>';
        return;
    }

    tbody.innerHTML = jobs.map(j => {
        const message = (j.lastRunMessage || '').replace(/\s+/g, ' ').trim();
        return `
        <tr class="${j.isEnabled ? '' : 'is-muted'}">
            <td>${esc(j.jobName)}${j.isEnabled ? '' : ' <span class="status-pill is-neutral">pasif</span>'}</td>
            <td>${esc(j.categoryName || '—')}</td>
            <td>${jobStatusPill(j.lastRunOutcome, j.isCurrentlyRunning)}</td>
            <td>${j.lastRunAt ? dayTime(j.lastRunAt) : '—'}</td>
            <td class="num">${jobDuration(j.lastRunDurationSeconds)}</td>
            <td class="sql" title="${esc(message)}">${esc(message) || '—'}</td>
        </tr>`;
    }).join('');
}

async function loadAgentJobs(opts = {}) {
    if (state.jobsInFlight) return;
    if (!opts.force && state.jobsLoaded) return;

    state.jobsInFlight = true;

    const status = $('jobsStatus');
    status.hidden = false;
    status.classList.remove('is-error');
    status.textContent = 'Yükleniyor…';

    try {
        const url = '/api/live/agent-jobs?instance=' + encodeURIComponent(state.instance || '');
        const data = await getJson(url);

        renderJobsSummary(data);
        renderJobsTable(data);

        state.jobsLoaded = true;
        status.hidden = true;
    } catch (err) {
        status.hidden = false;
        status.classList.add('is-error');
        status.textContent = 'Alınamadı: ' + err.message;
    } finally {
        state.jobsInFlight = false;
    }
}

// ------------------------------------------------------------------
// Sorgu detay çekmecesi
// ------------------------------------------------------------------

/** Bütün çekmece açıcılar (canlı oturum, prosedür tanımı, ...) aynı yükleme/hata iskeletini kullanır. */
function openDrawerShell(title) {
    $('drawer').hidden = false;
    $('drawerTitle').textContent = title;
    $('drawerSql').textContent = 'Yükleniyor…';
    $('drawerPlan').textContent = '';

    // openMissingIndexDrawer/openProcedureDrawer bu ikisini kapatıp yerine kendi istatistik
    // bloğunu koyuyor - buradan (oturum/sorgu planı) açılan bir sonraki
    // çekmecede eski hâline dönmesi ŞART, yoksa "Yürütme planı" başlığı
    // kaybolmuş ya da eski index istatistikleri asılı kalmış olur.
    $('drawerPlanSection').hidden = false;
    $('drawerStatBox').hidden = true;
}

async function openDrawer(sessionId) {
    openDrawerShell('SPID ' + sessionId);

    try {
        const url = `/api/live/session/${sessionId}/plan?instance=` +
                    encodeURIComponent(state.instance || '');
        const data = await getJson(url);

        setDrawerSql(data.sqlText);
        $('drawerPlan').textContent = data.planXml || '(plan alınamadı — istek bitmiş olabilir)';
    } catch (err) {
        $('drawerSql').textContent = 'Alınamadı: ' + err.message;
    }
}

/** Plan metnini (varsa) renklendirip basar - #drawerPlan'a DOKUNMAZ, o XML'dir, SQL değil. */
function setDrawerSql(text) {
    const el = $('drawerSql');
    if (text) el.innerHTML = highlightSql(text);
    else el.textContent = '(sorgu metni alınamadı)';
}

function closeDrawer() { $('drawer').hidden = true; }

// ------------------------------------------------------------------
// Zamanlayıcı ve olaylar
// ------------------------------------------------------------------

function restartTimer() {
    if (state.timer) clearInterval(state.timer);
    state.timer = null;

    // Sunucu yoksa zamanlayıcıyı hiç kurmuyoruz - yoksa saniyede bir
    // 404 alan bir döngü başlatmış oluruz (bkz. enterNoInstanceState).
    if (!state.instance) return;

    if (state.intervalSeconds > 0) {
        state.timer = setInterval(refresh, state.intervalSeconds * 1000);
    }
}

/*
    Tema: Açık / Koyu / Sistem. VARSAYILAN KOYU.

    Gerçek uygulama <head>'deki erken script'te oluyor (flaş yaşanmasın
    diye) - burası yalnızca seçicinin görünen değerini o kararla
    eşleştiriyor ve değiştiğinde hem <html data-theme>'i hem
    localStorage'ı güncelliyor. "Sistem" seçiliyken data-theme SİLİNİR -
    o zaman devreye CSS'teki prefers-color-scheme medya sorgusu girer,
    JS'in işletim sisteminin temasını izlemesine hiç gerek kalmaz.

    Hiç kayıt yoksa (ilk açılış) "koyu" gösteriyoruz - <head>'deki script
    de aynı varsayılanla data-theme="dark" basıyor, ikisi TUTARLI olmak
    zorunda; biri "sistem" sanıp diğeri "koyu" uygularsa seçici sayfanın
    gerçek temasıyla çelişen bir değer gösterir.
*/
function setupTheme() {
    const select = $('themeSelect');
    let saved = null;
    try { saved = localStorage.getItem('sqlmonitor-theme'); } catch { /* gizlilik modu */ }
    select.value = saved === 'system' ? 'system'
                  : (saved === 'light' || saved === 'dark') ? saved
                  : 'dark';

    select.addEventListener('change', () => {
        const choice = select.value;
        if (choice === 'system') {
            delete document.documentElement.dataset.theme;
        } else {
            document.documentElement.dataset.theme = choice;
        }
        try {
            // "system" burada SİLİNMİYOR, YAZILIYOR - varsayılan artık
            // "koyu" olduğu için boş bir kayıt bir sonraki açılışta
            // "sistem" değil "koyu" olarak okunurdu. Kullanıcının
            // "sistem"i BİLEREK seçtiğini kalıcı olarak işaretlememiz
            // gerekiyor, kaydın yokluğuyla karıştırmadan.
            localStorage.setItem('sqlmonitor-theme', choice);
        } catch { /* kaydedemedik, en azından bu oturumda tema doğru */ }
    });
}

function wireEvents() {
    setupTheme();

    $('instanceSelect').addEventListener('change', async (e) => {
        state.instance = e.target.value;
        state.lastTimelineAt = 0;   // instance degisti, cizelge hemen tazelensin
        state.eventEvidence = {};   // EventId global; eski sunucunun kanitini tasima

        // Bu sekmeler "bir kez yükle, tekrar isteme" mantığıyla çalışıyor
        // (bkz. loadMissingIndexes ve benzerleri) - instance değişmeden
        // bu doğru, ama değiştiğinde "yüklendi" bayrağı ESKİ sunucunun
        // verisini işaret eder. Sıfırlamazsak sekmeye dönüldüğünde yeni
        // sunucu yerine eski sunucunun bayat sonucu görünmeye devam eder.
        state.miLoaded = false;
        state.spLoaded = false;
        state.unusedLoaded = false;
        state.agLoaded = false;
        state.jobsLoaded = false;
        state.waitTrendLoaded = false;

        // RequiresLogin=true bir instance'a geçilmişse (ve kilit henüz
        // açık değilse) burada dur - refresh() 401 alıp giriş formunu
        // zaten gösterirdi, ama önden sormak fazladan bir başarısız
        // isteği önlüyor.
        const ok = await ensureAuthorized();
        if (ok) {
            refresh();
            reloadActiveTab();
        }
    });

    $('authForm').addEventListener('submit', handleAuthSubmit);
    $('authLogout').addEventListener('click', handleAuthLogout);
    $('authClose').addEventListener('click', handleAuthCancel);

    $('manageInstances').addEventListener('click', openInstanceManager);

    $('slackSave').addEventListener('click', async () => {
        const url = $('slackWebhook').value.trim();
        if (!url) { setSlackError('Webhook adresini yapıştır.'); return; }
        await saveSlackSetting(url);
    });

    $('slackTest').addEventListener('click', testSlackSetting);

    $('slackClear').addEventListener('click', async () => {
        if (!window.confirm('Slack webhook adresi silinsin mi?\n\nBildirimler duracak.')) return;
        await saveSlackSetting('');
    });
    $('instMgrClose').addEventListener('click', closeInstanceManager);
    $('instMgrAddNew').addEventListener('click', () => openInstanceForm(null));
    $('imCancel').addEventListener('click', closeInstanceForm);
    $('instMgrForm').addEventListener('submit', handleInstanceFormSubmit);
    $('imRequiresLogin').addEventListener('change', toggleImCredFields);

    $('instMgr').addEventListener('click', (e) => {
        if (e.target.id === 'instMgr') closeInstanceManager();
    });

    $('intervalSelect').addEventListener('change', (e) => {
        state.intervalSeconds = Number(e.target.value);
        restartTimer();
    });

    $('refreshNow').addEventListener('click', refresh);
    $('drawerClose').addEventListener('click', closeDrawer);

    // KPI şeridi saniyede bir yeniden çiziliyor (innerHTML baştan kuruluyor) -
    // dinleyiciyi TEK SEFERLİK olarak sabit üst kapsayıcıya (#kpis) koyup
    // olayı hedefe göre çözüyoruz. Her render'da 9 ayrı dinleyici takıp
    // sökmek yerine bu, hem daha basit hem sızıntısız.
    $('kpis').addEventListener('mousemove', handleSparkHover);
    $('kpis').addEventListener('mouseleave', hideSparkHover);

    $('drawer').addEventListener('click', (e) => {
        if (e.target.id === 'drawer') closeDrawer();
    });

    document.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape') return;

        // BUG (bulundu/düzeltildi): sıra önceden instMgr'ı authGate'ten
        // ÖNCE kontrol ediyordu. Sunucular ekranı açıkken, üstünde
        // düzenlediğin instance'ı RequiresLogin yaptığında giriş kapısı
        // onun ÜSTÜNE biner (z-index daha yüksek) - Esc o zaman altındaki
        // Sunucular ekranını kapatıyordu, görünürde hâlâ duran giriş
        // formu bir dokunuş daha bekliyordu. En üstteki katman önce kapanmalı.
        if (!$('authGate').hidden) { handleAuthCancel(); return; }
        if (!$('instMgrForm').hidden) { closeInstanceForm(); return; }
        if (!$('instMgr').hidden) { closeInstanceManager(); return; }
        closeDrawer();
    });

    document.querySelectorAll('.tab').forEach(tab => {
        tab.addEventListener('click', () => {
            document.querySelectorAll('.tab').forEach(t => t.classList.remove('is-active'));
            document.querySelectorAll('.tabpanel').forEach(p => p.classList.remove('is-active'));
            tab.classList.add('is-active');
            document.querySelector(`[data-panel="${tab.dataset.tab}"]`).classList.add('is-active');

            // Plan cache taraması pahalı - yalnızca sekme ilk kez açıldığında
            // yükleniyor, sonraki tıklamalarda tekrar sorgulanmıyor.
            if (tab.dataset.tab === 'indexanalysis') loadMissingIndexes();
            if (tab.dataset.tab === 'storedproc') {
                loadTopProcedures(state.spSort, state.spDir);
                loadUnusedProcedures();
            }
            if (tab.dataset.tab === 'alwayson') loadAlwaysOn();
            if (tab.dataset.tab === 'agentjobs') loadAgentJobs();
        });
    });

    document.querySelectorAll('#spSort button').forEach(btn => {
        // force YOK: zaten seçili sıralamaya (aynı yönde) tekrar tıklamak
        // boşuna tekrar sorgu göndermesin - taze veri isteyen zaten
        // "Yenile"ye basar. Hap her zaman 'desc' ister - "en kötüsü"
        // mantığı; yön değiştirmek istersen sütun başlığına tıkla.
        btn.addEventListener('click', () => loadTopProcedures(btn.dataset.sort, 'desc'));
    });

    document.querySelectorAll('#spTable thead th.sortable-th').forEach(th => {
        th.addEventListener('click', () => {
            const key = th.dataset.sort;
            const nextDir = (state.spSort === key && state.spDir === 'desc') ? 'asc' : 'desc';
            loadTopProcedures(key, nextDir);
        });
    });

    $('miRefresh').addEventListener('click', () => loadMissingIndexes({ force: true }));
    $('spRefresh').addEventListener('click', () => loadTopProcedures(state.spSort, state.spDir, { force: true }));
    $('unusedRefresh').addEventListener('click', () => loadUnusedProcedures({ force: true }));

    // Filtre sunucuya tekrar sormuyor - state.unusedData zaten elde, sadece
    // yeniden çiziyoruz.
    $('unusedStrongOnly').addEventListener('change', () => {
        if (state.unusedData) renderUnusedProcedures(state.unusedData);
    });
    $('agRefresh').addEventListener('click', () => loadAlwaysOn({ force: true }));
    $('jobsRefresh').addEventListener('click', () => loadAgentJobs({ force: true }));

    $('requestsTable').addEventListener('click', (e) => {
        const row = e.target.closest('tr[data-session]');
        if (row) openDrawer(Number(row.dataset.session));
    });

    // Sekme arka plandayken sorgu atmanın anlamı yok - izlenen sunucuya
    // boşuna yük bindirmeyelim.
    document.addEventListener('visibilitychange', () => {
        if (document.hidden) {
            if (state.timer) clearInterval(state.timer);
        } else if ($('authGate').hidden) {
            refresh();
            restartTimer();
        }
        // BUG (bulundu/düzeltildi): giriş kapısı açıkken bu dal koşulsuz
        // refresh()+restartTimer() çağırıyordu - refresh() 401 alıp
        // showAuthGate()'i TEKRAR tetikliyor, o da alanı yeniden
        // odaklıyordu (kullanıcı şifreyi yazarken sekme değiştirip geri
        // dönmüşse imleç/odak elinden alınıyordu). Kapı açıkken zaten
        // atılacak bir istek yok - sekmeler arası geçiş instance'ı
        // kilitli olmaktan çıkarmaz.
    });
}

(async function start() {
    // Tek doğruluk kaynağı #intervalSelect'in HTML'deki "selected"
    // seçeneği - state.intervalSeconds'ı burada koddan sabitlemiyoruz,
    // bkz. state tanımındaki not.
    state.intervalSeconds = Number($('intervalSelect').value);

    wireEvents();
    try {
        await loadInstances();
    } catch (err) {
        showErrors([{ panel: 'yapılandırma', message: err.message }]);
        return;
    }

    // Başlangıçta seçili instance (varsayılan/IsDefault olan) kilitliyse
    // önce giriş formu gösterilir - refresh() burada BİLEREK çağrılmıyor,
    // ensureAuthorized() zaten kilitliyken showAuthGate()'i tetikliyor.
    const ok = await ensureAuthorized();
    if (ok) {
        await refresh();
        restartTimer();
        loadWaitTrend();
    }
})();
