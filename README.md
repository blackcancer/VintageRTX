# VintageRTX — réécriture

Branche de développement : `dev/renderer-rewrite-20260921`. L'ancienne implémentation reste dans `dev/renderer-recovery-20260918` et l'historique Git. Les étapes de la réécriture continuent sur la même branche.

**État : R01b, données régionales, émissions configurables et premières requêtes GPU. Le rendu natif du jeu reste inchangé.** Les shaders de requête sont exercés dans des contextes de test ; la composition PBR de l'image du monde n'est pas encore raccordée. Ce build ne doit pas être présenté comme le mod photoréaliste terminé.

## Objectif

Lumières et réflexions cohérentes, réactives et aussi naturelles que possible, sans dénaturer les modèles de Vintage Story. Scènes intérieures/extérieures, géométries fines et animées, reflets hors écran, métaux rugueux, eau et verre. Les optimisations ne doivent pas supprimer silencieusement des sources ni attendre la stabilisation de tous les caches.

## Éléments présents

Le cœur indépendant du jeu comprend un registre de sources avec identités structurelles et frames immuables, des profils temporels déterministes, des transactions régionales, un ordonnanceur incrémental et un tracer CPU de référence. Les coordonnées monde sont conservées en double précision ; les données GPU sont relatives à une ancre entière.

L'adaptateur importe un sous-ensemble **explicitement limité de maillages opaques statiques** via l'API publique. Les régions GPU sont mises à jour indépendamment. Les autres géométries restent inconnues ou non prises en charge, jamais remplacées par des cubes supposés corrects. La traversée GLSL conserve les états clear, hit, unknown, unsupported et budget exhaustion.

L'émission se configure dans **`assets/vintagertx/config/emission.json`**, modifiable par les patches JSON natifs. Les profils incluent feu/torche, lanterne, lampe à huile, bougie, chandelier, émission stable/pilotée par le moteur et enveloppe de foudre. Les règles utilisent les codes runtime et les variantes, pas le nom supposé d'un fichier. Documentation et exemples : [EMISSION-ASSETS.md](docs/EMISSION-ASSETS.md).

Chaque frame lumineuse est maintenant transférée vers sa propre texture RGBA32F, **indépendamment de la disponibilité géométrique**. Les consommateurs doivent utiliser la même frame et la même ancre. Aucun vacillement supplémentaire n'est calculé dans les requêtes GPU. L'éclairage diffus ponctuel et ses occultations sont testés sur cette chaîne CPU→GPU ; les reflets complets et les émetteurs surfaciques restent à raccorder.

Commandes client : `.vrtxemissions` décrit le catalogue final, sa révision et ses erreurs ; `.vrtxrewrite` affiche les sources, régions et compteurs de transfert. Ces commandes n'annoncent pas un effet visuel absent.

## Compilation et validation

SDK .NET **10.0.401**, solution Visual Studio **`VintageRTX.slnx`**, client cible **Vintage Story 1.22.7**.

```powershell
dotnet test .\tests\VintageRTX.Core.Tests\VintageRTX.Core.Tests.csproj -c Release
$env:VINTAGE_STORY = 'C:\Chemin\Vers\Vintagestory'
dotnet test .\tests\VintageRTX.Client.Tests\VintageRTX.Client.Tests.csproj -c Release
```

Les tests du cœur n'ont pas besoin du jeu. Les tests client ont besoin des références et assets officiels, et les tests graphiques créent un contexte OpenGL caché. La CI exécute aussi les requêtes GLSL sur les paquets exportés par le C# de production. Un test de patch utilise le **vrai `ModJsonPatchLoader`** du client sur des assets en mémoire ; ce n'est pas un second patcher maison.

Références supplémentaires activées pour le client : **Newtonsoft.Json, OpenTK.Core, OpenTK.Graphics et OpenTK.Mathematics**, fournies par le jeu et non redistribuées. Les bibliothèques OpenTK.Windowing sont utilisées seulement par les tests pour leur contexte caché. Harmony et Skia ne sont pas activés.

Ne pas mélanger les fichiers `bin` de recovery et de rewrite. La sortie active est `src/VintageRTX.Client/bin/...`. Aucun ancien shader n'est importé automatiquement.

## Limites et réception

Voir [ARCHITECTURE.md](docs/ARCHITECTURE.md), [LIGHTING.md](docs/LIGHTING.md) et [R01-LIGHT-TRANSFER.md](docs/R01-LIGHT-TRANSFER.md). Les acquis de R00 restent documentés dans leur état historique ; ils ne décrivent pas seuls la situation actuelle.

Manquent notamment la capture complète de la matière du jeu, les passes PBR visibles, les géométries animées et alpha-testées, les attaches précises des mains et mèches, les émetteurs surfaciques, la foudre météo réelle, les reflets rugueux/hors écran et la reconstruction temporelle. Le chandelier est encore une source agrégée, sans multiplication supplémentaire du nombre de bougies. Les intensités sont des grandeurs relatives provisoires, non des mesures SI.

La CI ne livre pas d'archive annoncée comme mod final. Une compilation et des requêtes GPU réussies ne certifient ni les pixels d'un monde réel ni les budgets de performances sur le matériel du joueur.
