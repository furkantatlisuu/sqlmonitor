/*
    SqlMonitor - izleme hesabı
    ---------------------------------------------------------------
    Bu betik İZLENEN sunucuda çalışır.

    Sakın sysadmin kullanma. Bu uygulama sadece OKUR; ona göre yetki
    verelim. Elinde sysadmin olan bir bağlantı dizesi, uygulamada
    bulunacak ilk açıkta prod sunucunun tamamı demektir.

    VIEW SERVER STATE     -> tüm sys.dm_* DMV'leri
    VIEW ANY DEFINITION   -> nesne adları, indeks tanımları
    CONNECT ANY DATABASE  -> her DB'ye USE edebilme (SQL 2014+)
    VIEW ANY DATABASE     -> sys.databases'te hepsini görebilme
*/

USE master;
GO

DECLARE @loginName SYSNAME = N'sqlmonitor_ro';

/* Windows kimlik doğrulaması kullanacaksan bu bloğu değiştir:
       CREATE LOGIN [DOMAIN\svc_sqlmonitor] FROM WINDOWS;
   ve aşağıdaki şifreli bloğu sil. Domain hesabı daha güvenli. */

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @loginName)
BEGIN
    /* Şifreyi çalıştırmadan ÖNCE değiştir. */
    CREATE LOGIN [sqlmonitor_ro]
        WITH PASSWORD = N'BunuMutlakaDegistir!2026',
             CHECK_POLICY = ON,
             DEFAULT_DATABASE = [master];
END
GO

GRANT VIEW SERVER STATE    TO [sqlmonitor_ro];
GRANT VIEW ANY DEFINITION  TO [sqlmonitor_ro];
GRANT VIEW ANY DATABASE    TO [sqlmonitor_ro];
GRANT CONNECT ANY DATABASE TO [sqlmonitor_ro];
GO

/* Yedek geçmişini okuyabilmek için msdb'de okuma hakkı.
   Canlı ekrandaki "Backuplar temiz mi?" kuralı buna bakar. */
USE msdb;
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sqlmonitor_ro')
    CREATE USER [sqlmonitor_ro] FOR LOGIN [sqlmonitor_ro];
GO
ALTER ROLE db_datareader ADD MEMBER [sqlmonitor_ro];
GO

/*
   HATA GÜNLÜĞÜ - isteğe bağlı
   ---------------------------------------------------------------
   sys.sp_readerrorlog yalnızca securityadmin üyelerine açıktır.
   Ekranda error log paneli istiyorsan aşağıdaki satırın yorumunu
   kaldır. İstemiyorsan bırak; uygulama o paneli boş gösterir,
   hata vermez.

   Not: securityadmin gerçek bir yetki artışıdır (login şifresi
   değiştirebilir). Bunu vermek istemezsen alternatif, log dosyasını
   dışarıdan okuyan ayrı bir job yazmaktır.
*/
-- ALTER SERVER ROLE securityadmin ADD MEMBER [sqlmonitor_ro];

PRINT 'sqlmonitor_ro hazır. Şifreyi değiştirdiğinden emin ol.';
GO
