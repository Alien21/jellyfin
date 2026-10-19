# Sestavení a nasazení na jellyfin.server.lan

## Lokální build

V Git Bash, s Node.js 24+, npm 11+ a .NET 10 SDK na PATH:

```bash
cd /c/Git/Movies/jellyfin-web
bash ./build.sh

cd /c/Git/Movies/jellyfin
bash ./build_linux.sh
```

Volby sestavení a portable runtime jsou popsané v [README.build.md](README.build.md)
a [jellyfin-web/README.build.md](../jellyfin-web/README.build.md).
Výstupy vznikají v `_artifacts`; kompilace v `_output`, mezisoubory a logy
v `_temp`, výsledky testů v `_tests`.

## Co nahrát

Přímo do `/opt` na serveru nahraj `jellyfin/_artifacts/install.sh` a alespoň
jeden z těchto balíků podle toho, kterou komponentu chceš aktualizovat:

- Server: `jellyfin/_artifacts/Jellyfin.linux-x64.<verze>-alien21.<commit>[-dirty].tar.gz`
- Web: `jellyfin-web/_artifacts/jellyfin-web-<verze>-<commit>[-dirty].tar.gz`

Ke každému nahranému archivu je povinný odpovídající `.tar.gz.sha256`.
Pro aktualizaci jedné komponenty tedy nahraješ tři soubory včetně instalátoru,
pro obě komponenty pět. Přesné názvy vypíše build.

Bez výslovně zadaných cest skript hledá archivy vedle `install.sh`.
Povoluje nejvýše jeden serverový a nejvýše jeden webový archiv; alespoň
jeden z nich musí být přítomný. Více verzí stejné komponenty ve složce
znamená chybu. Pokud jsou dodány oba balíky, jejich verze musí souhlasit.
Komponentu bez dodaného archivu ponechá na místě: neinstaluje ji ani
nepřejmenovává do zálohy.

## Instalace

Následující příkazy spusť **na Linux serveru**:

```bash
cd /opt
sudo bash ./install.sh --check
sudo bash ./install.sh
```

`--check` ověří dodané archivy a současnou instalaci a vypíše plán.
Vlastní instalace ověří SHA256 i obsah balíků a připraví nové adresáře
jen pro vybrané komponenty. Při aktualizaci webu zachová současný
`config.json`. Zastaví službu a při aktualizaci serveru uloží snapshot
databází a konfigurace. Samostatná aktualizace webu tento snapshot
nevytváří ani konfiguraci a databáze neobnovuje. Potom vymění vybrané
komponenty, spustí službu a ověří její stav a HTTP health endpoint.
Při chybě vrátí vyměněné komponenty; po možném spuštění nového serveru
obnoví také uložené databáze a konfiguraci.

Výchozí health URL je `http://127.0.0.1:8096/health`. Pro vlastní port nebo Base URL:

```bash
sudo bash ./install.sh --health-url http://127.0.0.1:8096/jellyfin/health
```

Pokud máš ve složce více verzí, můžeš výslovně zadat cestu k jednomu
nebo dvěma archivům. Typ se pozná podle názvu souboru, pořadí je libovolné:

```bash
sudo bash ./install.sh /cesta/Jellyfin.linux-x64.13.0.0-alien21.abc123.tar.gz
sudo bash ./install.sh /cesta/jellyfin-web-13.0.0-abc123.tar.gz
sudo bash ./install.sh /cesta/jellyfin-web-13.0.0-abc123.tar.gz /cesta/Jellyfin.linux-x64.13.0.0-alien21.abc123.tar.gz
```

Použij skutečné názvy vytvořené buildem a jejich `.sha256` ponech vedle archivů.
Při výslovném výběru se ostatní archivy vedle instalátoru nepřidávají.
Přehled voleb zobrazí `bash ./install.sh --help`.

## Cíle a zálohy

Skript aktualizuje ověřenou existující instalaci Ubuntu 24.04 amd64 se službou
`jellyfin.service`. Vyžaduje Bash, GNU tar/coreutils, Python 3, curl, systemctl a flock.

| Obsah | Cíl |
| --- | --- |
| Celá self-contained serverová sada včetně .NET runtime a nativních knihoven | `/opt/Jellyfin/` |
| Celý produkční web | `/opt/UI/` |
| Databáze, pluginy a ostatní data | `/opt/data/` |
| Konfigurace | `/opt/config/` |
| Cache | `/opt/cache/` |
| Logy | `/opt/log/` |
| Volby spouštění služby | `/etc/default/jellyfin` |
| Snapshot databází a konfigurace `state.tar` a jeho `.sha256`, při změně serveru | `/var/backups/jellyfin/<release_id>/` |
| Původní verze nahrazovaných komponent | Sousední adresáře s příponou `.previous-<release_id>` |

Snapshot při aktualizaci serveru zahrnuje `/opt/config`, `/etc/default/jellyfin` a pouze aktivní
soubory `*.db` přímo v `/opt/data/data` včetně jejich souvisejících SQLite
souborů (`-wal`, `-shm`, případně `-journal`). Databázové soubory se vybírají
bez procházení podsložek. Obrázky, metadata, `SQLiteBackups`, cache a logy
se nezálohují ani neobnovují. Původní verze vybraných komponent zůstávají
zachované přejmenováním; ostatní komponenty zůstávají ve svých adresářích.

FFmpeg a ffprobe zůstávají v `/usr/lib/jellyfin-ffmpeg/`, preload knihovna
v `/usr/lib/jellyfin/libjemalloc.so`. Služba může spouštět `/opt/Jellyfin/jellyfin`
přímo nebo přes cestu či symlink, který se na tento soubor vyřeší. Kontrola
porovnává skutečné cíle cest, takže přijímá i starší logické cesty přes symlinky
do uvedených adresářů v `/opt`. Nastavení služby zůstává v `/etc/default/jellyfin`.
Zdrojové kódy, `node_modules`, `_temp`,
`_output` ani `_tests` se na server nenahrávají. Server používá přibalený
runtime .NET; vývojové SDK a Node.js potřebuješ pro lokální build.

Změna verze serveru může při prvním startu migrovat databázi. Při jeho
aktualizaci proto instalátor pro návrat uchovává uvedené databáze
a konfiguraci spolu s původními binárkami. Původní web uchovává, pokud
byl také vybrán k aktualizaci. Obnova databází je důležitá i po neúspěšném
startu nové verze serveru.
Po nasazení ověř dashboard, pluginy,
vyhledávání a přehrávání. Viz také
[oficiální postup zálohování a obnovy](https://jellyfin.org/docs/general/administration/backup-and-restore/).

## Vlastnictví a oprávnění

Instalátor nastavuje vlastnictví svých souborů na `alien21:plex` a skupině
povoluje čtení i zápis:

| Oblast | Adresáře | Běžné soubory | Spustitelné programy |
| --- | --- | --- | --- |
| Aplikace a web | `775` | `664` | `775` |
| Zálohy instalátoru | `770` | `660` | — |
| Obnovená konfigurace | `770` | `660` | `770` při zachované spustitelnosti |
| Obnovené databáze | `770` pro datové adresáře | `660` | — |

Soubor `/etc/default/jellyfin` má také `alien21:plex` a `660`.
Pro opravu oprávnění již nasazené instalace použij:

```bash
cd /opt
sudo bash ./install.sh --fix-permissions
```

Oprava zahrnuje aplikaci a web včetně jejich známých sousedních adresářů
`.new-*`, `.previous-*` a `.failed-*`, existující zálohy instalátoru,
celý strom `/opt/config` a `/etc/default/jellyfin`. U dat nastaví `770`
samotným adresářům `/opt/data` a `/opt/data/data`; soubory upraví pouze
u přímých aktivních databází a jejich SQLite sidecarů. Datovou složku
neprochází rekurzivně; obrázky a metadata v datech i cache a logy ponechá
beze změny. Oprava běží pod instalačním zámkem a jeho existujícímu souboru
nastaví `660`. Nepotřebuje archivy, nic nesestavuje a službu nerestartuje.

Existující `/run/lock/jellyfin-local-install.lock` se otevírá pouze ke čtení;
`flock` nad ním stále drží výhradní zámek. Soubor se nepřepisuje ani nenahrazuje,
takže další běhy sdílejí stejný zámek i při vlastnictví `alien21:plex` a zapnuté
linuxové ochraně `fs.protected_regular`. Pokud starší instalátor skončí na této
cestě chybou `Permission denied`, nahraj opravený `install.sh` a spusť jej znovu.
Archivy aplikace a webu kvůli této opravě není potřeba znovu sestavovat.

## Návrat na předchozí verzi

```bash
cd /opt
sudo bash ./install.sh --rollback
```

Bez ID se použije poslední úspěšné nasazení. Konkrétní zálohu zvolíš takto:

```bash
sudo bash ./install.sh --rollback '<release_id>'
```

Rollback vrátí pouze komponenty zahrnuté do daného nasazení: server
v `/opt/Jellyfin`, web v `/opt/UI` nebo obě. Ostatní komponenty ponechá
na místě. Pokud nasazení zahrnovalo server, obnoví také uložené databázové
soubory v `/opt/data/data`, konfiguraci v `/opt/config` a `/etc/default/jellyfin`.
Při návratu samotného webu konfiguraci a databáze neobnovuje.
Starší zálohy bez záznamu vybraných komponent se obnovují jako společné
nasazení serveru a webu. Ostatní obsah datového adresáře, obrázky,
metadata, cache a logy ponechá beze změny. Zálohy ponech do ověření
nasazení; jejich umístění a ID vypíše instalátor.

## APT balíky

Instalátor spravuje vlastní build v `/opt/Jellyfin` a `/opt/UI`; nevytváří `.deb`.
Evidence dpkg dále hlásí původní balíky `12.2+ubu2404`. APT aktualizuje
distribuční cesty, systémovou službu a `/etc/default/jellyfin`; vlastní adresáře
v `/opt` nejsou jeho přímým cílem. Po aktualizaci balíků proto ověř, že služba
i její volby stále míří na uvedené cesty v `/opt`, včetně případných symlinků.
Balík `jellyfin-ffmpeg8` se tímto instalátorem nenahrazuje.
