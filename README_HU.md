# LocaUltrator 0.8.7

Natív Windows WPF alkalmazás játékszövegek Gemini API-s és vágólapos fordításához. A 2.5.2-es forrásból a bevált natív felület, projekt/import/export, tokenvédelem, vágólapcsomag-kezelés, minőségkapu és játékfájl-műhely került át. Az Ulti, Codex App Server, helyi agent és régi AI-híd nincs a programban.

## Használat

1. Indítsd el a LocaUltratort, majd hozz létre projektet a forrásfájlokból.
2. A „Tokenvédelem” résznél nyisd meg a beépített promptot, majd válaszd az „1. Prompt + TXT az AI-nak” gombot. A kitöltött prompt a vágólapra és `TOKENMASZKOLASI_PROMPT.txt` néven a projekt forrás-pillanatképei mellé kerül.
3. Add a promptot és a megfelelő TXT-fájlt az AI-nak. Másold vágólapra kizárólag a visszakapott JSON-t, majd válaszd a „2. AI JSON-profil betöltése” gombot. A program helyben ellenőrzi a regexeket és a valódi projekttalálatokat, előnézetet mutat, majd csak jóváhagyás után aktiválja a profilt.
4. A „Gemini API beállítások” ablakban több kulcsot vehetsz fel, sorrendezhetsz és tesztelhetsz. Minden engedélyezett kulcs aktív a soros körforgásban; mellettük külön állapotjelző látható. A kulcsok minden módosítás után automatikusan mentődnek a `Dokumentumok\LocaUltrator\gemini-kulcsok.json` fájlba.
5. Kulcsonként válaszd ki a Free/Tier 1/Tier 2/Tier 3/egyéni használati szintet, majd másold be a Google AI Studio aktív Rate limits oldaláról a Gemini 3.7 Flash és – fizetős Tieren – a Gemini 3.1 Pro RPM/TPM/RPD értékeit. A limitek projektenként és modellenként külön működnek; az előfizetési szint módosítása azonnal mentődik.
6. Free kulcs kizárólag `gemini-3.7-flash` modellt használ. Tier 1–3 kulcsnál a program 503, 429, 404 és átmeneti 5xx hiba esetén dinamikusan válthat `gemini-3.7-flash` és `gemini-3.1-pro-preview` között. Más modell nem kerül API-kérésbe.
7. Válassz munkamódot: Automatikus, Gemini API vagy Vágólap. Automatikus módban modell- és kulcsváltás után, ha a Google szolgáltatása továbbra sem elérhető, a mentett vágólapcsomag következik.
8. Vágólapos munkánál másold az AI válaszát vissza a vágólapra. A figyelő az azonosított csomagot ellenőrzi, visszaépíti és azonnal menti; kézi feldolgozó és visszaállító gomb is van.
9. Ellenőrizd az „Ellenőrzendő” sorokat, futtasd a minőségkaput, majd exportálj.

Az API-kulcsok nem kerülnek a projektfájlba, URL-be vagy naplóba. Az elsődleges kulcstár a külön, kézzel szerkeszthető `gemini-kulcsok.json`; minden felülírás előtt `.bak` biztonsági másolat készül, a Windows Hitelesítőadat-kezelő pedig másodlagos biztonsági mentés. A JSON olvasható formában tartalmazza a titkos kulcsokat, ezért nem szabad megosztani vagy nyilvános tárhelyre feltölteni. A fordítás strukturált JSON-sémával készül; az API- és vágólapút ugyanazt a token- és fájlszerkezet-ellenőrzést használja.

## Frissítés

A grafikus telepítő ugyanazzal az AppId-val telepít minden további verziót. Meglévő telepítésnél automatikusan a korábbi mappát használja, kihagyja az útvonalválasztást, bezárja a futó példányt, és csak a programfájlokat frissíti. A `Dokumentumok\LocaUltrator` alatti projektek és a Gemini-beállítások megmaradnak.

## Forrás fordítása

Szükséges: .NET 8 SDK és Inno Setup 7 (vagy kompatibilis Inno Setup 6).

```powershell
dotnet restore LocaUltrator.sln --configfile NuGet.Config
dotnet run --project tests/LocaUltrator.Tests/LocaUltrator.Tests.csproj -c Release
dotnet publish src/LocaUltrator.App/LocaUltrator.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/portable-0.8.7
ISCC.exe installer/LocaUltrator.iss
```

## Támogatott közvetlen források

Az importáló elsődlegesen TXT, táblázatos TXT/CSV/TSV, címkézett párok, JSONL és `.lutext` forrásokat kezel veszteségmentes export-metaadatokkal. A játékfájl-műhely a laza szövegfájlokat felismeri; csomagolt vagy bináris játékformátumhoz külön, megbízható adapter szükséges.
