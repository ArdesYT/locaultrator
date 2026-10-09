# LocaUltrator 0.8.7 – valódi kulcsteszt és dinamikus Gemini modellútvonal

## Kulcsteszt

- A teszt először a hivatalos `v1beta/models/{model}` végponton ellenőrzi a kulcsot, a modell-hozzáférést és a `generateContent` támogatását.
- A Google 503-as kapacitáshibája többé nem jelenik meg „TERHELT” kulcsként. Ha a kulcs már igazolt, az állapota `MŰKÖDIK` marad.
- A tesztfordítás átmeneti hibánál ugyanazt a dinamikus modellváltást használja, mint a valódi fordítás.

## Kizárólagos modellhasználat

- A program csak `gemini-3.7-flash` és `gemini-3.1-pro-preview` modellt küldhet az API-nak.
- Free Tieren kizárólag a 3.7 Flash használható, mert a 3.1 Pro hivatalosan fizetős modell.
- Tier 1–3 és egyéni fizetős profilnál 503, 429, 404, 408 és átmeneti 5xx esetén automatikus 3.7 Flash ↔ 3.1 Pro váltás történik.
- A fordítás eredete mindig a ténylegesen használt modellt rögzíti.

## Hivatalos kvótadimenziók

- Kulcsonként és modellenként külön RPM, bemeneti TPM és RPD adható meg az AI Studio aktív Rate limits oldaláról.
- Az ütemező projekt+modell szinten védi az RPM-et, gördülő 60 másodperces ablakban a TPM-et, és programfutáson belül az RPD-t.
- A Tier-váltás nem ír kitalált RPM-et a mezőkbe; a pontos aktív limitek projekt-, modell- és fiókfüggők.
- A 429 csak az érintett projekt+modellt hűti le, így fizetős Tieren a másik modell használható maradhat.

## API-kompatibilitás

- Öt teljes próbálkozásig exponenciális visszavárás és jitter használható, ha nincs azonnal elérhető tartalékmodell.
- A Gemini 3.7 hivatalos ajánlásával összhangban a program nem küld `temperature`, `topP` vagy `topK` mintavételi paramétert.
