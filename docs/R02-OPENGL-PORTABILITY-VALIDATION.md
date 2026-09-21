# R02 — qualification du correctif OpenGL

Code exécuté : `eb638c925c77766716dca11bdd1e0cbfdb828faa`, branche `dev/renderer-rewrite-20260921`.
Ce document est postérieur au commit qualifié et ne modifie ni le renderer, ni les tests, ni la CI.

## Résultats vérifiés

Workflow **Renderer rewrite foundations**, run `35653943424` : succès des deux jobs.
- `core.trx` : 203 tests, 203 exécutés et réussis, 0 ignoré.
- `client.trx` : 59 tests, 59 exécutés et réussis, 0 ignoré.
- `gpu.log` : 14 tests GLSL réussis sous Mesa llvmpipe (LLVM 20.1.2).
- Références client officiel Vintage Story 1.22.7, SDK .NET 10.0.401, configuration Release.

Les onze noms des journaux fournis ont été recherchés individuellement dans `client.trx` ; tous ont `outcome=Passed` :

1. ResizeReallocatesOnlyOwnedTargetsAndFailureIsRecoverable
2. CompatibilityFrontBackPolygonModesAreRestoredIndependently
3. EveryImageFailureStageInvalidatesOutputAndCanRetryWithoutRelaxingChecks
4. FeedbackAndMissingOwnedInputsAreRejectedBeforeADraw
5. InvalidParametersAndOlderFramesCannotLeaveTheLastImageReady
6. ShaderCompileLinkAndUnallocatedDisposalPathsRemainUsable
7. LabRemainsOptInAndCanRecoverFromShaderFailureWithoutAWorldImageWrite
8. DirectHdrDoesNotDependOnPreviewExposureOrPreviouslyLitFrames
9. ForeignGlStateCannotChangeTheImageAndIsRestoredIncludingIndexedMasks
10. FullLabImageMatchesCpuTrianglesMaterialsAndFiniteSourceReference
11. GeometryRevisionWorldAndAnchorMismatchesCannotPublishOldOutput

Les neuf cas de `GlPortabilityRegressionTests` sont additionnels. Le test d'injection couvre cinq frontières, dont la restauration avant publication. Aucun test signalé n'a été retiré et aucun seuil de calcul/image n'a été abaissé.

## Couverture réelle

Workflow **Production line and branch coverage**, run `35653943566` : les deux campagnes instrumentées ont réussi avec les mêmes nombres de tests ; le contrôle final échoue parce que le seuil demandé de 100 % des branches n'est pas atteint.

`coverage-report/summary.json` :
- Toutes les sources C# de production instrumentables dans `src`, deux assemblies.
- Lignes : **1836 / 1836, 100 %**.
- Branches : **1814 / 1840, 98,5869565 %**.
- `missingSources: []`.
- **26 branches encore non couvertes**. Ne pas déclarer le workflow entièrement vert.
- `RewriteDrawState.cs` : 65/65 lignes, 22/22 branches.
- `RewritePolygonModes.cs` : 12/12 lignes, 8/8 branches.

La mesure ne couvre pas les branches GLSL et ne vaut pas réception en jeu. Les fichiers, nombres de branches et versions peuvent changer ; ces chiffres désignent exclusivement le commit ci-dessus.

## Déploiement préservé

Workflow **Development mod deployment and launch contract**, run `35653943435` : succès de ses deux jobs Ubuntu 24.04 et Windows 2025. Les builds Debug/Release, le paquet à deux DLL et les profils de lancement restaurés restent contrôlés. Aucun monde n'est ouvert par ces jobs.

Les fichiers de lancement, les propriétés de chemins et le csproj n'ont pas été modifiés par le correctif OpenGL. Voir `DEVELOPMENT-LAUNCH.md`.

## Archives contrôlées

Les empreintes SHA-256 des archives téléchargées correspondent aux digests GitHub ; leur vérification CRC réussit.

| Artifact | SHA-256 |
|---|---|
| `10663158948`, adapter | `a1226f7da27f36f70b0f77f6b6b5a7f66cab7f15a6841e119aa5e2faed53ff11` |
| `10663113989`, core | `b25058e25e5757c0e40dd90a2816238fe658422f16d3ecff756a75065d5e66c9` |
| `10663443211`, coverage | `d0c6b42679ba66443cfcc8e60f8f0655f5d6fa333ea29df2eeed951311de830b` |

## Limites explicites

Les erreurs de restauration et les hypothèses de tests non portables ont été corrigées et qualifiées sous Mesa. Aucun nouvel essai sur le GPU de l'utilisateur n'est déclaré, et ses journaux ne permettent pas d'identifier son fabricant ou les valeurs brutes des états du pilote.

**Le nouveau pipeline n'est toujours pas raccordé à l'image du monde.** Le laboratoire DirectImagePass est vérifié ; la capture des véritables surfaces et leur composition dans le renderer Vintage Story restent à intégrer. Un mod chargé, un laboratoire fonctionnel et un renderer de monde actif sont trois résultats distincts. Ce lot ne doit pas être présenté comme une activation du mode RTX du monde, ni comme un gain FPS.

Détails : `R02-OPENGL-PORTABILITY.md`.
