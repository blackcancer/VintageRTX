# R01c — résultats exécutés

Code qualifié : `e1f86d7c6e454b08d43ba62a1d4d5b7a9ba8409f`, sur `dev/renderer-rewrite-20260921`.

Workflow permanent **Renderer rewrite foundations**, run **35591529633**, terminé le 21 septembre 2026 : les deux jobs ont réussi.

- Job `106306782341` : **152 tests C# du cœur exécutés, 152 réussis, aucun ignoré**, puis **11 tests GLSL réussis** sous Mesa llvmpipe (LLVM 20.1.2).
- Job `106306990813` : compilation du cœur, de l'adaptateur et des tests contre le client officiel **Vintage Story 1.22.7**, SDK **10.0.401**, puis **18 tests client exécutés, 18 réussis, aucun ignoré**. Le catalogue copié dans le build a été comparé au fichier versionné.

Les compteurs sont ceux des sorties `core.trx` et `client.trx` contrôlées par la CI, et du résumé unittest dans `gpu.log`. Les nouvelles classes sont `FiniteEmitterSamplingTests`, `EmissionShapeAssetTests`, `EmissionMultiplicityTests`, `CandleRuntimeCountTests` et `test_finite_emitters.FiniteEmitterTests`. Les anciens tests de cette branche n'ont pas été supprimés et aucun seuil existant n'a été abaissé.

Les contrôles client comprennent le getter **réel** `BlockChandelier.CandleCount` pour la succession des variantes `candle0` à `candle8`, puis des retours vers des quantités inférieures ; ils examinent également les variantes `quantity` dans les assets de bougies au sol du client installé. Il ne s'agit pas d'une simulation de clics joueur dans un monde.

Le compteur de composantes modifie la fluctuation agrégée, jamais une seconde fois l'intensité totale fournie par le jeu. Les mèches ne sont pas encore des sources géométriques indépendantes. L'estimateur sphérique est testé en C# et en GLSL ; son test de pénombre utilise une visibilité analytique connue, tandis que les tests existants qualifient séparément la vraie traversée régionale.

## Limites de réception

Aucun nouveau rendu PBR de monde ni gain FPS n'est déclaré. La branche affiche encore l'image native du jeu et prépare les données pour le nouveau renderer. L'intégration des passes d'image, les attaches des mains, les positions/rotations des mèches, les masques alpha, les reflets complets et la GI restent à développer.

Aucune nouvelle branche, aucune fusion et aucune modification de `main` ou de `dev/renderer-recovery-20260918` dans ce lot. Ce document est un commit de documentation postérieur au code testé, pas une extension rétroactive de la certification à du nouveau code.

Détails et patches : [R01-FINITE-EMITTERS.md](R01-FINITE-EMITTERS.md). Contrat de base des assets : [EMISSION-ASSETS.md](EMISSION-ASSETS.md).

Preuves du run : https://github.com/blackcancer/VintageRTX/actions/runs/35591529633
