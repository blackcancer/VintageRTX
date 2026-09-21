# Optimisation OPT-01 — topologie liquide active

Date : 21 septembre 2026. Branche cible : `dev/renderer-recovery-20260918`.
Référence figée : `cdcc00ae42e9e4ddc279ac10cd5f4b72237b6ceb`.

## Périmètre livré

Ce lot concerne le coût CPU du solveur de surface liquide. Il ne change aucun shader, profil graphique, intensité, portée de lumière, nombre de rayons ou seuil de validation d'image. Il ne constitue pas une optimisation globale du GPU ni une réception des scénarios en jeu.

Le solveur conserve son pas fixe à 120 Hz, les mêmes buffers numériques, la même grille, les mêmes équations, les mêmes événements, la même suite pseudo-aléatoire et le même ordre des cellules actives. La nouvelle classe partielle `LiquidSurfaceTopology` met en cache des données géométriques, pas des résultats de simulation.

Avant : `Step`, `ComputeNormals`, le vent, les comptages de pluie et de bulles parcouraient à répétition la grille complète. Les recherches de voisinage refaisaient les contrôles de limites, de profil et de hauteur de base à chaque sous-pas, alors que ces relations ne dépendent pas de l'évolution des vagues.

Après : les cellules actives sont regroupées dans l'ordre original. Chaque stencil conserve les quatre voisins connectés ; une limite réfléchissante référence la cellule centrale. La pluie utilise une table de successeurs reproduisant exactement l'ancienne recherche circulaire, et non une nouvelle distribution des impacts. Les indices par profil conservent l'ordre de sélection original des bulles. Les propriétés de connexion et les tables sont invalidées par `SetSurfaceCell` et `ClearSurfaceCell`, puis reconstruites une fois au prochain besoin. Un changement de vent ou une nouvelle image ne reconstruit pas la topologie.

`ClearSurfaceCell` remet déjà à zéro les deux banques de hauteur/vitesse : parcourir toutes les cellules sèches à chaque pas n'est plus nécessaire. Les cellules liquides restent toutes simulées, y compris hors écran. Il n'y a ni mise en veille fondée sur la visibilité ni suppression des petites perturbations.

Le coût supplémentaire des stencils et des indices est d'environ **0,44 MiB** pour une grille de 128 × 128 cellules, hors petits en-têtes de tableaux. La construction initiale et les reconstructions après modification restent des parcours O(N). Le formatage de la texture RGBA et certains effacements de buffers restent également complets ; le coût d'une grille sèche n'est donc pas nul.

## Référence indépendante et identité des sources

`tools/performance/compare_liquid.py` extrait la classe historique du blob Git **`ad2b4728ef4272d73024c53eb32b116e1d7446d9`**, contrôlé avant compilation. Elle est renommée pour cohabiter avec le solveur candidat ; elle n'est pas reconstruite à partir de celui-ci. Les modèles physiques auxiliaires sont communs et inchangés dans ce lot.

Le projet temporaire est créé sous `tests/obj/liquid-performance`, sans modifier les sources suivies. La référence est lue par `git show` ou récupérée au commit exact avec contrôle du blob. Une différence de source ne peut pas actualiser silencieusement l'oracle.

Sources CPU qualifiées, SHA-256 :

- `LiquidSurfaceSimulation.cs` : `93a349cdff0c49b5b1a8242b347f31ca846c0edeea48806e2d3757fac1dca4dd`.
- `LiquidSurfaceTopology.cs` : `d201677b46cfe4fb77f39f76ec8dec3382e2a1c0b136e06d7e5201ee0c735029`.

Six dimensions sont testées : 1 × 1, 1 × 11, 13 × 1, 17 × 13, 37 × 19 et 128 × 128. Les 360 mises à jour de chaque cas combinent domaines discontinus, profils différents, profondeurs, vent, pluie, bulles, impacts, suppressions/réactivations de cellules et pas de frame variables.

Résultat des deux premières comparaisons : **2 160 mises à jour et 6 239 880 texels comparés**, sorties RGBA32F identiques bit à bit. Les vitesses, échantillons de cellule, nombres de pas, admissions d'impacts et compteurs d'événements sont également comparés. Cette vérification porte sur ces trajectoires, pas sur toutes les entrées possibles du jeu.

## Mesures CPU reproductibles

Le banc mesure `Advance(1/60)` puis `WriteGpuTexture`, soit le solveur et le formatage RGBA complet. Il **exclut** les observations du monde, l'upload OpenGL, le rendu, le temps d'image total et les FPS. Le résultat ne doit pas être appliqué comme un pourcentage de gain du mod complet.

Pour chaque occupation : cinq paires A/B, ordre alterné, 30 mises à jour de chauffe, puis 240 mises à jour mesurées. La médiane des cinq coûts moyens est publiée, avec chaque observation brute. La compilation graduée .NET est désactivée pour les deux implémentations dans ce seul exécutable, pas dans le jeu. Le premier pas incluant la construction de topologie est mesuré séparément. Le benchmark ne mesure pas encore un flux continu de mutations topologiques.

Machine des deux premières campagnes : AMD EPYC 7763, 4 processeurs logiques alloués, Ubuntu 24.04.5, runtime .NET 10.0.12 / SDK 10.0.401. Les valeurs sont des mesures du runner, pas du poste Windows du joueur.

Campagne CPU seule `35574160082`, artefact `10626679200`, `liquid-performance.json` :

| Cellules actives / 16 384 | Avant (ms/mise à jour) | Après (ms/mise à jour) | Coût CPU retiré |
|---|---:|---:|---:|
| 0 | 0,2157 | 0,0463 | 78,5 % |
| 1 | 0,2164 | 0,0463 | 78,6 % |
| 64 | 0,2241 | 0,0514 | 77,1 % |
| 1 024 | 0,3243 | 0,1217 | 62,5 % |
| 4 096 | 0,6497 | 0,3609 | 44,5 % |
| 16 384 | 1,9511 | 1,3833 | 29,1 % |

La première campagne `35573231181` donne respectivement 78,5 %, 78,5 %, 77,1 %, 63,0 %, 48,1 % et 32,8 %. Toutes les observations sont conservées : les écarts entre runners/exécutions ne sont pas masqués par la sélection du meilleur résultat. Les deux versions allouent zéro octet managé dans la boucle chronométrée après chauffe ; ce lot ne revendique pas une allocation supprimée là où elle était déjà nulle.

## Contrôles et limite d'intégration constatée

Sur chacune des deux premières campagnes : compilation Release du mod et des deux projets de tests réussie contre Vintage Story 1.22.7, **35/35** préflight, **155/155** régressions sélectionnées, **125/125** tests de classes liquides, et **27/27** tests GLSL. Les sélections C# ont des recoupements et ne constituent pas une couverture totale du dépôt.

Les cinq nouveaux tests de topologie vérifient la réutilisation lors du vent/mouvement, l'invalidation après un lot d'éditions, l'absence de résurrection de vagues dans une cellule réactivée et les grilles étroites. Les tests historiques n'ont pas été retirés ou assouplis.

Le RenderLab complet a dépassé son délai de 120 s dans ces deux campagnes. Les services GPU préparatoires ont pris environ 86,2 s puis 96,9 s avant la scène et les captures principales. Le premier essai comprenait aussi un changement expérimental de shader ; le second n'en comprenait plus. Les journaux ne permettent donc pas d'attribuer ce délai au seul essai GPU. Le statut global de ces deux campagnes est **échec**, malgré la réussite des comparaisons CPU ; il n'est pas présenté comme une qualification complète.

La campagne suivante isole l'exécution graphique à froid avant la charge du benchmark CPU et arrête les serveurs de compilation après le build. Elle conserve le délai, les 640 × 360 pixels, les 30 frames de chauffe, les 60 frames mesurées et tous les contrôles de publication. Son résultat doit être lu dans le workflow, indépendamment de ce document.

## Essai GPU non retenu

Une suppression des lectures de reconstruction dont le poids bilinéaire vaut exactement zéro a été testée sur OpenGL. Douze cas ont conservé les mêmes sorties HDR et les mêmes décisions de repli. Les lectures géométriques ont diminué sur certaines grilles alignées à un tiers de résolution, mais pas sur les grilles à demi-résolution ou toutes les dimensions impaires.

Ce comptage ne démontre pas un gain de temps GPU : une branche et la compilation peuvent aussi avoir un coût. **L'essai a été retiré avant la compilation du lot CPU seul.** Les shaders livrés restent identiques à `cdcc00ae`. Aucun gain GPU n'est attribué à cette expérimentation.

## Suite prioritaire de l'optimisation — non livrée par OPT-01

1. **Mesure GPU par étape et périmètre complet.** Le compteur global commence actuellement dans `RenderDisplayPass`, après `UpdateVoxelTexture` et `LiquidSurfaceRuntime.Update`. Distinguer albedo MRT, copies, miroir d'entités, uploads, visibilité/rebond, filtre et composition. Conserver la lecture différée des timestamps déjà présente dans `RenderPerformanceMonitor`, sans attente forcée du CPU. Compter les pixels qui retracent le transport complet après échec de reconstruction. Comparer profils fixes avant toute adaptation automatique.
2. **Chemin CPU des lumières.** `CopyDynamicPointLights` crée encore un `HashSet` et un `Dictionary` par collecte et trie via LINQ ; `EntityLightCollector` fabrique un tableau pour purger les identités. Réutiliser ces stockages et mettre en cache les caractéristiques stables, sans figer la position ni retarder l'extinction. Tester la sélection, le désappariement et les changements de dimension avec les mêmes données avant/après.
3. **Données transférées et état OpenGL.** Réduire les zones réellement uploadées et les copies redondantes, en préservant les générations et la propriété des textures. Ne pas supprimer les sauvegardes d'état emprunté aux autres mods sans preuve qu'une sauvegarde extérieure couvre tous les retours et exceptions.
4. **Parcours GPU et occupations compactes.** Comparer une représentation binaire compacte aux 64 octets d'occupation fine par bloc, conserver séparément la couverture alpha, et accélérer seulement les cellules dont la vacuité ou la solidité est prouvée. Un budget épuisé ou une sortie de couverture ne devient jamais une visibilité confirmée.
5. **Travail par matériau et par signal.** Séparer les calculs invariants des profils liquides, les chemins de surfaces sèches et les besoins spéculaires. Une restriction à des tuiles/pixels nécessite une preuve conservative ; la présence d'un occultant ou d'une source hors écran doit rester prise en compte.

Chaque lot doit produire une comparaison de sortie, une mesure du travail réellement retiré et un coût temporel du périmètre complet. Les gains microbench, le temps GPU et les FPS restent trois mesures distinctes. Les défauts d'image et les 21 scénarios en jeu ne sont pas réputés corrigés par ce lot de coût CPU.

## Reproduction locale courte

Avec `VINTAGE_STORY` configuré pour le client 1.22.7 :

```powershell
dotnet test .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release --filter "FullyQualifiedName~Liquid"
python .\tools\performance\compare_liquid.py --output-directory .\tests\artifacts\performance
```

Ces commandes ne lancent pas les scénarios longs en jeu. L'exécutable comparatif nécessite Python, Git et le SDK .NET du projet ; il ne demande pas de nouvelle bibliothèque au mod.
