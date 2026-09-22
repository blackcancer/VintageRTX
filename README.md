## Contrôle global et tests en monde chargé (R05)

` .vrtx on ` / ` .vrtx off ` / ` .vrtx toggle ` contrôlent le mod pendant la session.
` .vrtxtest start ` lance un smoke test A/B/A dans le monde actuellement chargé.
La portée, les captures, le lanceur Windows et les limites de validation sont décrits dans
[docs/R05-CONTROL-INGAME.md](docs/R05-CONTROL-INGAME.md).
Un résultat de laboratoire ne constitue pas une réussite en jeu. Le lancement local du client
complet n'a pas abouti à un monde testé ; voir les preuves fournies avec R05.

# VintageRTX — réécriture

Branche de développement : `dev/renderer-rewrite-20260921`. L'ancienne implémentation reste dans `dev/renderer-recovery-20260918` et l'historique Git. Les étapes de la réécriture continuent sur la même branche.

**État : R04, éclairage diffus local et premiers matériaux conducteurs raccordés au terrain natif.** Le raccordement est activé par défaut et consomme les surfaces réellement rasterisées, sans reconstruction de l'albédo depuis une image déjà éclairée. Terrain opaque, sols herbeux, entités opaques et objets utilisant le shader `standard` sont reliés. Les blocs métalliques homogènes explicitement déclarés dans les assets peuvent maintenant évaluer le conducteur GGX dans `chunkopaque`. Trois presets initiaux couvrent les blocs décoratifs neufs en cuivre, or et argent. Les reflets secondaires et l'optique de l'eau ne sont pas encore remplacés. Ce build reste expérimental, pas le renderer photoréaliste terminé.

**[R04 — matériaux, patches, publication et limites](docs/R04-NATIVE-MATERIALS.md).** Le [raccordement R03](docs/R03-NATIVE-WORLD.md) conserve sa documentation historique. `.vrtxworld status` décrit l'état réel de liaison ; `.vrtxworld off` rétablit la voie native sans retirer le mod. Le laboratoire R02 reste facultatif et distinct du rendu du monde.

## Objectif

Lumières et réflexions cohérentes, réactives et aussi naturelles que possible, sans dénaturer les modèles de Vintage Story. Scènes intérieures/extérieures, géométries fines et animées, reflets hors écran, métaux rugueux, eau et verre. Les optimisations ne doivent pas supprimer silencieusement des sources ni attendre la stabilisation de tous les caches.

## Éléments présents

Le cœur indépendant du jeu comprend un registre de sources avec identités structurelles et frames immuables, des profils temporels déterministes, des transactions régionales, un ordonnanceur incrémental et un tracer CPU de référence. Les coordonnées monde sont conservées en double précision ; les données GPU sont relatives à une ancre entière.

L'adaptateur importe un sous-ensemble **explicitement limité de maillages opaques statiques** via l'API publique. Les régions GPU sont mises à jour indépendamment. Les autres géométries restent inconnues ou non prises en charge, jamais remplacées par des cubes supposés corrects. La traversée GLSL conserve les états clear, hit, unknown, unsupported et budget exhaustion.

L'émission se configure dans **`assets/vintagertx/config/emission.json`**, modifiable par les patches JSON natifs. Les profils incluent feu/torche, lanterne, lampe à huile, bougie, chandelier, émission stable/pilotée par le moteur et enveloppe de foudre. Les règles utilisent les codes runtime et les variantes, pas le nom supposé d'un fichier. Documentation et exemples : [EMISSION-ASSETS.md](docs/EMISSION-ASSETS.md).

Chaque frame lumineuse est transférée vers sa propre texture RGBA32F, **indépendamment de la disponibilité géométrique**. Les consommateurs doivent utiliser la même frame et la même ancre. Aucun vacillement supplémentaire n'est calculé dans les requêtes GPU. L'éclairage direct diffus, conducteur GGX et les sources sphériques finies sont exercés par le laboratoire. Le monde utilise maintenant le diffuseur et, sur les blocs homogènes explicitement déclarés, le conducteur GGX ; les reflets complets de l'environnement restent à raccorder.

Commandes client : `.vrtxemissions` décrit le catalogue final, sa révision et ses erreurs ; `.vrtxrewrite` affiche les sources, régions et compteurs de transfert ; `.vrtxlightlab on` affiche le laboratoire synthétique (désactivé par défaut), avec `off`, `dark` et `lit` pour le masquer ou changer son éclairage. Le laboratoire n'est pas le rendu PBR du monde.

## Compilation et lancement dans Visual Studio

SDK .NET **10.0.401**, solution Visual Studio **`VintageRTX.slnx`**, client cible **Vintage Story 1.22.7**. Définir **VintageRTX.Client** comme projet de démarrage.

Les trois profils historiques sont disponibles : **Vintage Story Client**, **Vintage Story Client (quick creative)** et **Vintage Story Client (menu)**. Ils utilisent le dossier d'installation résolu et ajoutent le chemin de mods de la configuration Debug ou Release. F5 n'exige plus de copie manuelle de la DLL.

```powershell
# VINTAGE_STORY conserve votre installation habituelle.
dotnet build .\src\VintageRTX.Client\VintageRTX.Client.csproj -c Debug
```

Sortie directement chargeable : **`src/VintageRTX.Client/bin/Debug/Mods/vintagertx`**, ou `Release` selon la configuration. Elle contient `VintageRTX.dll`, **`VintageRTX.Core.dll`**, `modinfo.json` et les assets. Le sous-dossier `net10.0` n'est plus intercalé dans cette sortie client. Le cœur et les tests gardent leurs sorties SDK ordinaires.

La résolution de `VINTAGE_STORY` accepte à nouveau la racine d'installation et un sous-dossier direct `Lib` ou `Mods`, comme recovery. Une propriété MSBuild explicite `VintageStoryPath` reste prioritaire. Pour exécuter les tests client, conserver la variable sur la racine car leur chargeur isolé lit l'environnement directement.

Détails des profils, du démarrage hors Visual Studio, des builds isolés et des contrôles : **[DEVELOPMENT-LAUNCH.md](docs/DEVELOPMENT-LAUNCH.md)**. Un raccourci normal du jeu ne reprend pas automatiquement les arguments F5 ; ne pas confondre compilation locale et installation dans les Mods personnels.

## Validation

```powershell
dotnet test .\tests\VintageRTX.Core.Tests\VintageRTX.Core.Tests.csproj -c Release
dotnet test .\tests\VintageRTX.Client.Tests\VintageRTX.Client.Tests.csproj -c Release
```

Les tests du cœur n'ont pas besoin du jeu. Les tests client ont besoin des références et assets officiels, et les tests graphiques créent un contexte OpenGL caché. La CI exécute aussi les requêtes GLSL sur les paquets exportés par le C# de production. Un test de patch utilise le **vrai `ModJsonPatchLoader`** du client sur des assets en mémoire ; ce n'est pas un second patcher maison.

La campagne de déploiement Windows/Linux vérifie les sorties Debug/Release, les profils et la résolution des deux DLL depuis le paquet. Elle ne lance aucun monde. Le seuil de couverture 100 % et les tests de rendu sont indépendants de cette campagne.

Références supplémentaires activées pour le client : **Newtonsoft.Json, OpenTK.Core, OpenTK.Graphics et OpenTK.Mathematics**, fournies par le jeu et non redistribuées. Les bibliothèques OpenTK.Windowing sont utilisées seulement par les tests pour leur contexte caché. Harmony et Skia ne sont pas activés.

Ne pas mélanger les fichiers `bin` de recovery et de rewrite. Aucun ancien shader n'est importé automatiquement et aucun ancien mod installé n'est supprimé par le build.

## Limites et réception

Voir [ARCHITECTURE.md](docs/ARCHITECTURE.md), [LIGHTING.md](docs/LIGHTING.md), [R01-LIGHT-TRANSFER.md](docs/R01-LIGHT-TRANSFER.md) et [R02-DIRECT-IMAGE.md](docs/R02-DIRECT-IMAGE.md). Les acquis des lots antérieurs restent documentés dans leur état historique.

Manquent notamment les normal maps et matériaux physiques complets du monde, la composition PBR multirebond, les géométries animées et alpha-testées, les attaches précises des mains et mèches, la foudre météo réelle, les reflets rugueux/hors écran et la reconstruction temporelle. Le chandelier est encore une source agrégée, sans multiplication supplémentaire du nombre de bougies. Les intensités sont des grandeurs relatives provisoires, non des mesures SI.

Les paquets R03/R04 sont des builds expérimentaux installables, jamais annoncés comme un mod final. Une compilation et des requêtes GPU réussies ne certifient ni les pixels d'un monde réel ni les budgets de performances sur le matériel du joueur.
