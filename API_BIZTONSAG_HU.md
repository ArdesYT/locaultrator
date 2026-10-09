# Gemini API-megvalósítás

- Végpont: `v1beta/models/{model}:generateContent`.
- Hitelesítés: kizárólag `x-goog-api-key` fejléc; a kulcs nem kerül URL-be, projektdokumentumba vagy naplóba.
- Kulcstár: elsődlegesen a kézzel szerkeszthető `Dokumentumok\LocaUltrator\gemini-kulcsok.json` fájl tárolja a teljes kulcslistát és a használati profilokat; mentés előtt `.bak` készül. A Windows Hitelesítőadat-kezelő másodlagos biztonsági másolat.
- Automatikus mentés: a kulcs, név, engedélyezés, használati szint, RPM és projektcsoport módosítása automatikusan fájlba kerül; a használati szint változása azonnal mentődik.
- Körforgás: minden engedélyezett kulcs aktív. A fordítási kötegek sorban, nem párhuzamosan járják körbe a kulcslistát.
- Kvótacsoport: a Google RPM/TPM/RPD és költési kvótái projektenként, nem kulcsonként működnek. A program projekt+modell állapotot vezet, és az azonos Cloud-projekthez tartozó kulcsok legalacsonyabb beállított limitjét használja.
- Profilok: Free, Tier 1, Tier 2 és Tier 3. A Tier nem határoz meg univerzális RPM-et; a Gemini 3.7 Flash és 3.1 Pro aktív RPM/TPM/RPD értékeit az AI Studio Rate limits oldaláról kell bemásolni.
- Ütemezés: legalább 750 ms globális szünet, modellenként `60 / RPM` időköz, gördülő 60 másodperces becsült bemeneti TPM-védelem, programfutáson belüli RPD-számlálás és `Retry-After` alapú 429 lehűtés.
- Modellek: kizárólag `gemini-3.7-flash` és `gemini-3.1-pro-preview`. Free Tieren csak 3.7 Flash; fizetős Tieren dinamikus 3.7 Flash ↔ 3.1 Pro váltás.
- Gondolkodás: alapértelmezés `high`, mindkét engedélyezett modellhez `thinkingLevel`. A 3.7 kompatibilitása miatt `temperature`, `topP` és `topK` nincs elküldve.
- Kulcsteszt: először a hivatalos `models.get` végpont igazolja a kulcsot és a `generateContent` hozzáférést. Egy későbbi 503/429 nem minősíti hibásnak vagy terheltnek a kulcsot.
- Válasz: `application/json` és kötegenként engedélyezett sorazonosítókat tartalmazó `responseJsonSchema`.
- Ellenőrzés: blokkolás, jelöltek száma, `finishReason=STOP`, JSON-szerkezet, idegen/dupla/hiányzó azonosítók, tokenmaszkok és fizikai sorvégek.
- Újrapróbálás: hálózati hiba, HTTP 408, 429 és 5xx esetén exponenciális backoff, jitter, `Retry-After`, legfeljebb négy újrapróbálás, majd – ha a Tier engedi – dinamikus modellváltás.
- Futás alatti kizárás: érvénytelen, kiszivárgott, jogosulatlan, számlázás nélküli vagy elfogyott Prepay-egyenlegű kulcs nem kap újabb köteget ugyanabban a futásban.
- Állapotok: működik, tesztelés, kvóta, hibás/kiszivárgott kulcs, nincs jogosultság, számlázás, modellhiba, túlterhelés, szerverhiba, időtúllépés és hálózati hiba.
- Automatikus munkamód: ha nincs használható API-projekt, a program ugyanazokat a tokenvédett sorokat mentett vágólapcsomagként adja át.
- Korlát: 190 másodperces HTTP-időtúllépés és 16 MiB maximális válasz.
- Hibás vagy hiányos eredmény soha nem ír felül kész fordítást; a sor `Ellenőrzendő` állapotba kerül.

Megjegyzés: a Google szerint a tényleges rate limit modellenként, használati szintenként és fiókállapot szerint változhat, nem garantált. A pontos aktuális értéket mindig a Google AI Studio Rate limits oldaláról kell átírni a kulcshoz.
