# LocaUltrator 0.8.3 – javítások

## Egységes sötét kezelőfelület

- A Gemini API-beállítások teljes háttere ugyanazt a sötét színvilágot használja, mint a főablak.
- A jelszómező, jelölőnégyzetek, görgethető felületek és felugró ablakok világos szöveget használnak sötét háttéren.
- Windows 10/11 alatt a natív címsor is sötét módot kér a rendszertől; régebbi rendszeren biztonságosan a rendszerbeállítás marad érvényben.

## Fájlhoz igazított AI-tokenmaszkolás

1. Nyiss meg vagy hozz létre egy projektet.
2. A `TOKENVÉDELEM` résznél válaszd az `1. Prompt + TXT az AI-nak` gombot.
3. A program a beépített promptsablont kitölti a projekt tényleges tokenjelöltjeivel és a felismert TXT-szerkezettel. A kitöltött prompt a vágólapra kerül, valamint `TOKENMASZKOLASI_PROMPT.txt` néven a projekt `Forrás-pillanatképek` mappájába, a források mellé mentődik.
4. Add a promptot és a megfelelő TXT-fájlt az AI-nak.
5. Másold vágólapra kizárólag az AI által visszaadott JSON-objektumot, majd válaszd a `2. AI JSON-profil betöltése` gombot.
6. A LocaUltrator helyben ellenőrzi a JSON-sémát, a .NET-regexek biztonságát, a példákat, az átfedéseket és a valódi projekttalálatokat. Aktiválás előtt részletes előnézetet mutat.
7. A jóváhagyott profil a projekt `Tokenprofilok` mappájába is archiválódik, majd minden forrás- és fordítássort újramaszkol. A megváltozott vagy hiányos tokenű kész fordításokat `Ellenőrzendő` állapotba teszi.

A program továbbra sem használ Ulti-, Codex- vagy helyi agent-közvetítőt. A fordítás közvetlenül a beállított Gemini API-val történik.

## Telepítés és frissítés

A grafikus telepítő stabil alkalmazásazonosítót használ. Ha korábbi LocaUltrator-telepítést talál, ugyanazt a mappát frissíti, a projekt- és felhasználói beállításokat változatlanul hagyja.

## Ellenőrzések

- Release fordítás: 0 figyelmeztetés, 0 hiba.
- Automatizált magtesztek: 4/4 sikeres.
- Főablak és Gemini API-ablak WPF indulási tesztje: sikeres.
- Önálló és telepített EXE indulási tesztje: sikeres.
- Első telepítés, helyben frissítés és eltávolítás: sikeres.
