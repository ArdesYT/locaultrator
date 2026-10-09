# LocaUltrator 0.8.4 – teljes munkafolyamat helyreállítása

## Több Gemini API-kulcs

- Legfeljebb 20 elnevezett kulcs vehető fel, törölhető, sorrendezhető és engedélyezhető.
- Külön beállítható az aktív/elsődleges kulcs; minden kulcs külön és együtt is tesztelhető.
- A kulcsok külön Windows Hitelesítőadat-kezelő rekordokban tárolódnak, titok nem kerül projektbe, URL-be vagy naplóba.
- Kulcs-, jogosultság-, kvóta-, hálózati és átmeneti szerverhiba esetén szabályozott kulcsváltás működik. A használt kulcs neve bekerül a fordítás eredetébe, maga a kulcs nem.
- Fontos: az azonos Google Cloud-projekthez tartozó kulcsok közös projektkvótát használhatnak.

## Dinamikus API–vágólap munkamód

- Projektenként választható `Automatikus: API → vágólap`, `Gemini API` vagy `Vágólap` mód.
- Automatikus módban a program végigpróbálja az engedélyezett kulcsokat, majd API-elérhetetlenségnél tokenvédett vágólapcsomagot készít.
- A módválasztás azonnal a projektbe mentődik.

## Vágólapcsomagok, figyelés és mentés

- Visszakerült a csomag készítése, újramásolása, kézi feldolgozása és visszaállítása.
- Több párhuzamosan várakozó csomag használható; mindegyik azonosítóval és sorszintű azonosítókkal rendelkezik.
- Az automatikus vágólapfigyelő csak ismert várakozó csomag válaszát fogadja el, a kiküldött promptot nem téveszti össze válasszal.
- A csomag létrehozása, feldolgozása és visszaállítása azonnal mentődik, ezért újraindítás után folytatható.
- Visszakerült az Insert/F8/F9/Ctrl+Enter gyorsbillentyű, az opcionális globális működés és az állapot-HUD.

## Ellenőrzés

- Az API és a vágólapos út ugyanazt a tokenmaszk-, sortörés- és szerkezetkaput használja.
- Automatizált teszt ellenőrzi a 429 utáni kulcsváltást és a vágólapcsomag mentés–újratöltés–feldolgozás folyamatát.
- A grafikus telepítő stabil AppId-val a már telepített példányt ugyanabban a mappában frissíti, a projektek és beállítások megtartásával.
