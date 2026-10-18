# Lokální sestavení serveru

Potřeba je .NET 10 SDK, Git, Bash, GNU tar, gzip a `sha256sum`. Ve Windows spusť příkazy v Git Bash; na Linuxu v běžném Bashi. SDK vybírá `global.json`.
Pokud SDK není na `PATH`, můžeš zvolit existující portable SDK bez globální instalace:

```bash
DOTNET_BIN='/c/cesta/k/dotnet/dotnet.exe' bash ./build_linux.sh
```

Pro běžné `dotnet build` a Rider přidej stejný adresář SDK do svého vývojového prostředí. Skript systémový `PATH` nemění.

Skript sestaví samostatně spustitelný Linux server v konfiguraci Release a nic nenasazuje.

```bash
bash ./build_linux.sh
```

Výchozí architektura je `linux-x64`. Jinou zvolíš pomocí `--runtime linux-arm64` nebo `--runtime linux-arm`. Křížové sestavení z Windows vytváří Linux ELF program; přímo ve Windows ho nespouštěj.

```bash
bash ./build_linux.sh --runtime linux-arm64
bash ./build_linux.sh --help
```

Výstupy jsou sjednocené i pro běžné `dotnet build`, `dotnet test` a Rider:

| Složka | Obsah |
| --- | --- |
| `_temp/bin`, `_temp/obj` | SDK výstupy jednotlivých projektů a mezisoubory |
| `_temp/logs` | Logy posledního běhu skriptu |
| `_temp/test-sdk` | Oddělené SDK výstupy testů spuštěných skriptem na hostitelském systému |
| `_output/net10.0/<runtime>/publish` | Kompletní self-contained publish serveru |
| `_tests` | Výsledky testů; běžný `dotnet test` je ukládá do podsložky podle projektu |
| `_artifacts/<runtime>/net10.0/Jellyfin` | Hotová složka serveru |
| `_artifacts/Jellyfin.<runtime>.<verze>-alien21.<commit>[-dirty].tar.gz` | Stejný obsah v archivu s jedinou kořenovou složkou `Jellyfin/` |

Vedle archivu vzniká soubor `.sha256`. Archiv i hotová složka obsahují `build-info.json` s verzí SDK, architekturou, UTC časem, větví, úplným hashem výchozího commitu a příznakem změněné pracovní kopie. Sestavení nezacommitovaných úprav je povolené; takový balík má v názvu `-dirty`. Verze serveru z `SharedVersion.cs` se při sestavení nepřepisuje. Hash commitu proto u změněné pracovní kopie označuje její základ, nikoli přesnou podobu všech zabalených zdrojů.

## Testy

`--tests` před zabalením spustí testy na systému, kde sestavuješ. Výchozí je celé řešení; projektem a filtrem lze rozsah omezit:

```bash
bash ./build_linux.sh --tests
bash ./build_linux.sh --tests \
  --test-project tests/Jellyfin.Server.Implementations.Tests/Jellyfin.Server.Implementations.Tests.csproj \
  --test-filter 'FullyQualifiedName~WebSocketConnectionTests'
```

Testy mají samostatné hostitelské restore výstupy, aby nepřepsaly závislosti Linux publish. Skript nenechává spuštěné MSBuild ani kompilační servery a při selhání restore, publish nebo testů skončí chybou. Server nespouští.

Restore probíhá standardně při každém běhu. Pro opakované sestavení po úpravě zdrojů lze použít `--skip-restore`, pokud už byly obnovené závislosti pro stejný runtime a případný stejný testovací projekt. Po změně balíčků, SDK či runtime proveď znovu běh bez této volby.

## Obsah balíku

Balík zahrnuje celý serverový publish, .NET runtime, Linux nativní knihovny, interní setup stránku, prostředky API dokumentace a licenci. `wwwroot` z projektu serveru obsahuje tyto interní prostředky; samostatný klient `jellyfin-web` se sestavuje ve svém repozitáři. FFmpeg/FFprobe, produkční konfigurace, databáze a média součástí balíku nejsou. Postup spojení serveru s webem a nasazení popisuje `README.deploy.md`.

Nové SDK výstupy všech projektů vznikají pod `_temp`; vedle zdrojových projektů nevznikají nové `bin` ani `obj`. Skript nahrazuje pouze svůj konkrétní publish a hotovou složku pro zvolený runtime.

Pokud je v repozitáři instalační skript, úspěšný build jej zkopíruje do `_artifacts/install.sh` vedle archivů. Do serverového archivu se nebalí.
