# LocaUltrator 0.8.6 – külön kulcsfájl és azonnali mentés

## Megbízható kulcsmegőrzés

- A teljes Gemini-kulcslista elsődlegesen a `Dokumentumok\LocaUltrator\gemini-kulcsok.json` fájlban marad meg.
- A fájl olvasható JSON, ezért kézzel szerkeszthető és egyszerűen biztonsági menthető.
- A programból közvetlenül megnyitható, kézi mentés után pedig egy gombbal újratölthető.
- Minden atomi felülírás előtt `gemini-kulcsok.json.bak` biztonsági másolat készül.
- Ha a kézzel szerkesztett JSON hibás, a program nem írja felül és nem törli a korábbi tartalmát.
- A korábbi Windows Hitelesítőadat-kezelőben tárolt kulcsok kulcsfájl hiányában automatikusan átkerülnek az új fájlba; a Windows-tár másodlagos biztonsági mentésként megmarad.

## Automatikus mentés

- A kulcs neve, titka, engedélyezése, RPM-je és projektcsoportja szerkesztés után automatikusan mentődik.
- A Free/Tier 1/Tier 2/Tier 3/egyéni előfizetési szint kiválasztása és az ahhoz tartozó kezdő RPM azonnal mentődik.
- Kulcs törlése és a körforgási sorrend módosítása szintén azonnal bekerül a fájlba.

## Biztonsági megjegyzés

A kézzel szerkeszthetőség miatt a JSON olvasható formában tartalmazza az API-kulcsokat. A fájlt nem szabad megosztani, nyilvános tárhelyre vagy verziókezelőbe feltölteni.
