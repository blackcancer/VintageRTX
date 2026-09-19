# Régressions signalées : reproduction, correctifs et qualification — 19 septembre 2026

Branche : `dev/renderer-recovery-20260918`.
Base du retour utilisateur : sources de récupération présentes à `9b7db633edbd60852430e3993c8b5d8c4bb0d4af`, sans prétendre identifier le commit local exécuté par l'utilisateur.
Correctif des dépendances natives du laboratoire : `9acea0a9b56e360f9d6c48031b4b6744f5ac8bd4`.
Correctifs C# ordinaires publiés après validation : **`bc5a5c84e89ca586c7354a273c258597db774264`**.

Aucune fusion dans main. Ce lot corrige un défaut du collecteur d'entités et les contrats de tests devenus incompatibles avec les réparations précédentes. Il ne modifie aucun shader de production, aucun seuil d'acceptation d'image, aucun seuil de performance et aucun paramètre du test RenderLab.

## 1. Reproduction avant correction

Le nouveau rapport comporte notamment 14 échecs C# hors monde, un dépassement de délai RenderLab et un résultat générique en échec pour `cave-interior`. Le marquage « Périmé » affiché sur plusieurs lignes ne permet pas de les écarter : les 14 échecs C# ont été reproduits.

Le run **35430087805**, job **105862985913**, exécute les sept classes complètes : **98 tests, 84 réussis, 14 échoués, aucun ignoré**. Le test RenderLab échoue également, mais avec une exception nulle dans le collecteur d'entités, pas avec le même timeout Windows que le rapport. Artefact de reproduction : `reported-regressions-9acea0a9b56e360f9d6c48031b4b6744f5ac8bd4`, ID **10580951018**.

La campagne précédente de 55 contrôles ne couvrait pas ces sept classes dans leur intégralité et n'exécutait pas ce test RenderLab. Une CI verte sur cette sélection ne démontrait donc pas que la nouvelle liste d'échecs était corrigée.

Le premier lancement de la nouvelle CI avait été bloqué avant ces tests par le projet RenderLab qui tentait de copier des DLL Windows depuis un client Linux. La condition MSBuild par système d'exploitation corrige cette préparation sans rendre les bibliothèques Windows facultatives sur Windows.

## 2. Correction de production : monde partiellement initialisé

`EntityLightCollector.Collect` parcourait directement `api.World.LoadedEntities.Values`. Les doubles de monde utilisés par plusieurs tests fournissaient un joueur mais aucune collection d'entités. La même exception remontait à `CopyDynamicPointLights`, puis au callback de rendu exercé par RenderLab.

Le collecteur vérifie désormais la présence du monde, du joueur, de sa position et de la collection d'entités avant de la parcourir. Sans données exploitables, il retourne une liste vide après avoir purgé les identités retenues. La collecte des lumières exposées par les tableaux du moteur reste un chemin distinct : elle n'est pas désactivée par l'absence d'une collection d'entités dans le banc de test.

Le code de l'émetteur est également résolu une seule fois et contrôlé avant son utilisation. Une entité sans code exploitable ne provoque pas un accès nul à `ToString`.

Ces changements ont supprimé les exceptions reproduites dans `DisplayFallbacksAcceptMissingGBufferAndLiveDynamicSurface`, `DynamicLightNullBuffersPrimaryReorderAndStableSelectionAreDeterministic`, `MatricesDynamicLightsSunAndDisabledVoxelBindingAreDeterministic`, ainsi que dans l'initialisation des services de rendu par RenderLab. Ils ne démontrent pas que le timeout Windows du rapport avait exactement cette cause.

Aucune nouvelle bibliothèque ni référence externe n'est nécessaire dans le mod.

## 3. Contrats de tests réparés, sans rétablir les anciens défauts

| Domaine | Cause de l'échec | Contrat conservé ou renforcé |
|---|---|---|
| Miroir et limites sans contexte GL | Le test exigeait une exception désormais évitée par la validation de projection. | Refus local de la passe, renderer non fautif et pile de projection intacte. |
| Spéculaire | Un test imposait de rebrancher le plafonnement empirique retiré du calcul direct. | Fresnel matériau, filtrage de l'empreinte des normales, diagnostics conservés ; ancien appel de compression interdit. |
| PBR des entités | Assertions sur un ancien ternaire et d'anciennes variables. | Payload signé, capture RGB/profondeur, exclusion de l'overlay et priorité de l'albedo exact avant le repli dynamique. |
| Ombres | Assertions sur l'ancienne dilation des trous de géométrie et l'exemption des lampes proches de la caméra. | Aucune géométrie inventée ; lectures catégorielles, compatibilité du plan receveur et nouvelle requête de visibilité si le masque voisin ne convient pas. |
| Positions des lumières | Le test attendait le déplacement d'une source de 10,004 vers 10 pour stabiliser l'historique. | L'admissibilité de l'historique ne modifie plus la position physique des sources ; les mouvements importants continuent de l'invalider. |
| Eau | Anciennes lectures interpolées de positions et ancien calcul actif de reconstruction des rives. | Lectures exactes de géométrie, interface devant la profondeur opaque et profondeur liquide au même pixel. Le reste de reconstruction héritée doit rester désactivé. |
| Candidats lumineux | Un compteur attendait les quatre entrées brutes, y compris une entrée nulle et une non finie. | Deux candidats réellement valides ; aucune lumière fictive comptée. |
| Émetteur mal formé | Le test indexait une deuxième lumière après avoir fourni un bloc sans code, que le résolveur rejette. | Liste inchangée avec la première lanterne conservée ; rejet explicite du candidat mal formé. |
| Changements de blocs | Fixture sans joueur et attente de l'ancien rebuild intégral immédiat. | Dimension active requise, modifications coalescées, traitement de la file puis actualisation des émetteurs et du cache lumineux sans rebuild intégral. |

Le test de cycle de vie couvre désormais explicitement une dimension étrangère et l'absence de joueur. Il vérifie ensuite la publication de la torche après `ProcessDirtyBlocks`, et les indicateurs de rafraîchissement lumineux. Il ne se contente pas de changer un nombre attendu.

Deux noms évoluent pour exprimer le comportement réel :

- `TemporalHistoryPreservesSubCentimetreLightPositionsAndRejectsMotion` ;
- `BlockChangeLifecycleCoalescesSameDimensionEditsAndRefreshesEmitters`.

Certaines assertions de raccordement restent textuelles. Elles ne sont pas présentées comme une preuve visuelle. Aucun test n'est marqué Ignore pour rendre cette campagne verte.

## 4. Nouveaux tests GLSL exécutables

`tests/materials/test_reported_materials.py` ajoute trois tests qui exécutent les fonctions extraites du shader de production dans un contexte OpenGL, en complément des assertions C# :

1. Un receveur dynamique sans albedo exact conserve sa propre couleur lorsque le voxel derrière lui passe du rouge au vert. Le résultat est comparé à une référence numérique indépendante. Une mutation qui supprime le retour dynamique réintroduit la contamination et est détectée.
2. L'albedo exact est prioritaire uniquement à profondeur compatible. Mauvaise profondeur, valeur NaN ou capture désactivée retrouvent le repli attendu.
3. Une empreinte de normale variable élargit la rugosité filtrée sans abaisser sa valeur d'origine ; une normale constante reste inchangée et tous les résultats restent finis et bornés.

Le premier lancement de ces nouveaux tests a révélé une déclaration manquante de `inverseFrameSize` dans le banc, pas dans le shader du mod. Elle a été ajoutée avec la dimension réelle de sa cible 1 × 1. Le workflow a refusé la publication jusqu'à réussite de tous les tests.

## 5. Preuves de validation avant publication

Run **35431316522**, job **105866322775**, conclu avec succès. Les sources corrigées préparées depuis `2ba6408a7ae868ea0005458ac183defe583fc60a` ont été compilées et testées avant leur publication en `bc5a5c84e89ca586c7354a273c258597db774264`.

| Contrôle | Résultat observé |
|---|---|
| Compilation Release du mod et du projet MSTest avec les références officielles Vintage Story 1.22.7 | Réussite, zéro erreur, deux avertissements |
| Compilation du projet RenderLab | Réussite |
| Sept classes complètes du rapport | **98 / 98**, aucun ignoré |
| `ProductionShaderCompilesAndRendersHeadlessly` | **1 / 1**, durée rapportée environ 1 min 35 s |
| Campagne précédente des blocages | **35 / 35**, aucun ignoré |
| Cible RGB MRT avec vrai contexte caché | **1 / 1** |
| GLSL de parcours et oracle rayon/boîte | **4 / 4** |
| GLSL de visibilité et métadonnées | **4 / 4** |
| GLSL des matériaux, dont les trois nouveaux tests | **9 / 9** |
| GLSL du transport secondaire | **5 / 5** |

Les quatre suites Python totalisent **22 tests**, tous réussis. Ne pas additionner les lignes C# pour annoncer un nombre de tests uniques : les sept classes et la campagne courte ont des recoupements. Cette sélection n'est toujours pas la totalité des tests du dépôt.

Environnement : Ubuntu 24.04, SDK .NET 10.0.401, client officiel Linux 1.22.7, Mesa llvmpipe LLVM 20.1.2, EGL et Xvfb. Les deux avertissements subsistants concernent la nullabilité de `RuntimeScenarioProbe.IsCaveSolid` et une référence XML de commentaire dans `FilmicDisplayRenderer`.

Artefact de validation : `reported-repair-2ba6408a7ae868ea0005458ac183defe583fc60a`, ID **10580578255**. Il contient les journaux, les TRX et les identités de source, pas le client du jeu.

## 6. Portée exacte du succès RenderLab

Le test original est conservé : timeout MSTest de **120 000 ms**, définition **640 × 360**, **60** frames mesurées après la préparation du laboratoire, critères de matériaux, de géométrie modifiée et de dynamique liquide inchangés. Le délai de 180 secondes du processus CI est une garde extérieure ; il ne remplace pas le timeout du test.

Le test compile et utilise les shaders de production sur une scène de laboratoire synthétique. Ce n'est pas un lancement d'un monde Vintage Story, ni une mesure du GPU Windows de l'utilisateur. Le succès de cette scène ne clôt pas le timeout Windows initial et ne valide pas `cave-interior`.

## 7. Contrôles permanents et essai Windows ciblé

La CI `reported-regressions.yml` exécute les sept classes via le fichier partagé `tests/reported-regressions.runsettings`, puis le véritable test RenderLab. Les rapports doivent contenir les tests effectivement exécutés et tous réussis ; une sélection vide ou des tests ignorés ne suffisent pas. La CI `renderer-recovery.yml` conserve les contrôles courts et les quatre suites GLSL, y compris les trois ajouts de ce lot.

Les deux recettes de migration et le workflow temporaire de publication sont retirés après livraison. La compilation ordinaire ne nécessite aucun script de transformation des sources.

Après sauvegarde du travail local et avec `VINTAGE_STORY` déjà défini :

```powershell
git fetch origin
git switch dev/renderer-recovery-20260918
git pull --ff-only
git log -1 --oneline

dotnet restore .\tests\VintageRTX.Test\VintageRTX.Test.csproj --source https://api.nuget.org/v3/index.json
dotnet test .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release --no-restore --settings .\tests\reported-regressions.runsettings --logger "trx;LogFileName=reported-regressions.trx"

dotnet restore .\tools\VintageRTX.RenderLab\VintageRTX.RenderLab.csproj --source https://api.nuget.org/v3/index.json
dotnet test .\tools\VintageRTX.RenderLab\VintageRTX.RenderLab.csproj -c Release --no-restore --filter "FullyQualifiedName~ProductionShaderCompilesAndRendersHeadlessly" --logger "trx;LogFileName=renderlab.trx"
```

Ces commandes reconstruisent les projets concernés. Elles ne sélectionnent pas les scénarios `InGameRealCaseTests`. Les résultats précédents affichés « Périmé » ne remplacent pas le nouveau rapport TRX.

## 8. Reste ouvert

L'extrait récent de `cave-interior` ne contient que le code de retour 1 et un renvoi au dossier d'artefacts. Aucune nouvelle capture, mesure de rebond ou sortie d'erreur détaillée n'y permet d'attribuer cette exécution à une cause précise. Les défauts visuels et de performances précédemment signalés restent ouverts jusqu'à leur vérification sur la bonne révision et les artefacts de cette exécution.

Ne pas affirmer que la garde `LoadedEntities` résout ce scénario, que le timeout Windows est éliminé ou que le renderer entier est qualifié. Les attaches lumineuses, la géométrie animée des occultants, la réflexion rugueuse complète et la qualification des scènes réelles restent des chantiers distincts.
