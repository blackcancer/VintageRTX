# OPT-01 — publication et qualification du lot CPU

Date : 21 septembre 2026. Branche : `dev/renderer-recovery-20260918`.

Code ordinaire publié : **`8c3bc26297ba65ab0e96e251f50e940cdfcab0de`**.
Base de comparaison : **`cdcc00ae42e9e4ddc279ac10cd5f4b72237b6ceb`**.

## Qualification ayant autorisé la publication

Run **`35574818174`**, job **`106254175794`**, artefact **`10627424649`**. Toutes les étapes ont réussi, y compris la vérification des résultats et le push sans force. Le fichier `published-commit.txt` de l'artefact identifie le commit ci-dessus.

SHA-256 de l'archive de preuves GitHub : `4350bb7a8cd98920fc6a1fbe3bf1d992f663f33fbda15149923046e124207b27`.

| Contrôle | Résultat |
|---|---|
| Build Release, mod + MSTest + RenderLab, références officielles Vintage Story 1.22.7 / SDK 10.0.401 | Réussite ; deux avertissements préexistants du mod |
| `blocker-smoke.runsettings` | 35 / 35, aucun ignoré |
| `reported-regressions.runsettings` | 155 / 155, aucun ignoré |
| Classes sélectionnées par `FullyQualifiedName~Liquid` | 125 / 125, aucun ignoré |
| Quatre suites GLSL | 27 / 27 |
| `ProductionShaderCompilesAndRendersHeadlessly` | Réussite en **116,705 s**, délai inchangé de 120 s |
| Comparaison au solveur historique figé | 2 160 mises à jour, 6 239 880 texels, RGBA32F identique bit à bit, vitesses et événements comparés |
| Shader `display.frag` | Identique à la référence : aucun essai GPU conservé |

Les sélections C# se recoupent : ne pas les additionner comme des tests uniques. Il ne s'agit ni de la totalité des tests du dépôt, ni d'une exécution des 21 scénarios dans Vintage Story.

Le RenderLab a été exécuté avant le benchmark CPU, avec un répertoire de cache Mesa neuf et sans serveur de compilation .NET actif. Sa définition, ses frames, ses seuils et son délai sont inchangés. Ce run utilise un **Intel Xeon 6973P-C à quatre processeurs logiques**, contrairement aux deux premiers runs sur AMD EPYC 7763. Il ne prouve donc pas que le seul ordre des étapes explique le retour sous 120 s. Les deux échecs précédents sont conservés dans le rapport OPT-01 ; la faible marge actuelle du laboratoire reste visible.

## Mesures de la campagne de publication

`Advance(1/60)` plus `WriteGpuTexture`, cinq paires A/B, 30 mises à jour de chauffe, puis 240 mesurées ; médiane des cinq coûts moyens. Même runtime et même processus pour chaque paire. Compilation graduée désactivée pour les deux solveurs dans le benchmark uniquement. Les fichiers JSON conservent chaque observation et le coût du premier pas.

| Cellules actives / 16 384 | Historique (ms/mise à jour) | Optimisé (ms/mise à jour) | Réduction CPU |
|---|---:|---:|---:|
| 0 | 0,13816 | 0,03107 | 77,5 % |
| 1 | 0,13791 | 0,03114 | 77,4 % |
| 64 | 0,14290 | 0,03423 | 76,0 % |
| 1 024 | 0,21026 | 0,08058 | 61,7 % |
| 4 096 | 0,43123 | 0,23635 | 45,2 % |
| 16 384 | 1,37942 | 0,90358 | 34,5 % |

Les deux versions allouent zéro octet managé dans la boucle chronométrée après chauffe. Ces valeurs **excluent les lectures du monde, les uploads OpenGL, le rendu et le temps d'image complet**. Aucune hausse de FPS en jeu ni baisse de temps GPU n'est revendiquée. Les variantes de coût CPU des deux premières campagnes, de 29,1 à 78,6 %, sont publiées séparément plutôt que masquées.

Les SHA-256 des deux fichiers CPU sont identiques aux deux premières campagnes :

- `LiquidSurfaceSimulation.cs` : `93a349cdff0c49b5b1a8242b347f31ca846c0edeea48806e2d3757fac1dca4dd`.
- `LiquidSurfaceTopology.cs` : `d201677b46cfe4fb77f39f76ec8dec3382e2a1c0b136e06d7e5201ee0c735029`.

## Contrôles permanents et portée

Le workflow en lecture seule `performance-regressions.yml` exécute les 125 tests liquides et la comparaison historique sur les sources du commit, sans recette de transformation. Il échoue sur une différence numérique, un test absent, ignoré ou échoué ; les durées CPU du runner sont publiées, mais ne sont pas transformées en seuils FPS arbitraires.

Les fichiers de staging et le workflow ponctuel d'écriture sont retirés. Le commit de nettoyage/documentation ne modifie pas le moteur qualifié. Les workflows Renderer recovery et Installable recovery delivery restent indépendants et doivent être contrôlés sur ce dernier commit : le succès du run de publication ne préjuge pas de leur résultat ultérieur.

Aucune bibliothèque supplémentaire n'est activée. Le solveur garde son pas à 120 Hz, son énergie, sa pluie, ses bulles et ses impacts. Les surfaces hors écran restent actives. Les optimisations GPU, les allocations de collecte des lumières, les uploads partiels et la qualification de tous les scénarios en jeu restent ouverts. Les priorités et la méthode sont détaillées dans `2026-09-21-performance-opt01.md`.
