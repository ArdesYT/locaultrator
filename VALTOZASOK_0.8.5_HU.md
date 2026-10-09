# LocaUltrator 0.8.5 – kulcsállapot és kvótavédett körforgás

## Felületi javítások

- Teszteléskor már nem tiltódik le és nem fehéredik ki a teljes beállítóablak.
- A lista, listaelemek és minden információs ToolTip egységes sötét hátteret, világos szöveget kapott.
- Minden kulcs mellett külön színes állapotjelző, rövid állapotnév és részletes sötét súgóbuborék látható.

## Kulcskörforgás és limitek

- Minden engedélyezett kulcs alapból aktív, a kötegek soros körforgásban használják őket.
- Nincs egyszerre induló tíz kérés: a program globális és projektcsoportos várakozást alkalmaz.
- Kulcsonként megadható Free, Tier 1, Tier 2, Tier 3 vagy egyéni használati szint, az alkalmazásoldali RPM és a Google Cloud-projektcsoport.
- Azonos projektcsoporthoz tartozó kulcsok közös kvótát és a legóvatosabb beállított RPM-et használják.
- A 429 `Retry-After` a teljes projektcsoportot lehűti; külön projektcsoport következő kulcsa közben sorosan használható.

## Modell és gondolkodás

- A támogatott szöveges Gemini modellek legördülő menüből választhatók.
- Az új alapértelmezett gondolkodási szint `high`.
- Gemini 3 modelleknél `thinkingLevel`, Gemini 2.5 modelleknél kompatibilis `thinkingBudget` készül.

## Hibadiagnosztika

- Külön felismerés készült érvénytelen, kiszivárgott és jogosulatlan kulcsra, számlázásra/elfogyott Prepay-egyenlegre, 429 kvótára, 404 modellhibára, 503 túlterhelésre, 5xx szerverhibára, időtúllépésre és hálózati hibára.
- A tartósan hibás kulcsok a futás végéig kimaradnak; az átmeneti hibák backoff és jitter után újrapróbálhatók.

## Ellenőrzés

- Automatizált tesztek ellenőrzik a teljes kulcskörforgást, a közös projekt RPM-várakozását, a 429 lehűtést, a hibakategóriákat és a Gemini 2.5/3 gondolkodási paramétereit.
