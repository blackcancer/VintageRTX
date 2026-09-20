# Campagne en jeu — captures numériques et fin d’exécution

Date : 20 septembre 2026. Branche : `dev/renderer-recovery-20260918`.

## Résultat utilisateur, avant ce lot

Le dernier tableau fourni comporte **21 scénarios, 2 réussites et 19 échecs**. `exterior-roof` et `held-light` réussissent. `many-lights-stress` dure 12,4 minutes et `nonstandard-geometry` 12,6 minutes. Les sept variantes RenderLab en jeu échouent. Les autres lignes ne fournissent que le code de retour agrégé 1.

Le tableau ne contient ni les lignes FAIL détaillées de ces nouvelles exécutions, ni leurs TRX, ni une identité du binaire exécuté. Les anciennes captures de `20260920-091101-cave-interior` sont une autre exécution : ne pas attribuer leurs valeurs GPU/couleur aux nouvelles lignes sans preuve. Les deux longues durées sont compatibles avec la limite de 12 minutes du harnais, mais ne prouvent pas leur cause.

## Changements de production publiés dans eb7200dd

### 1. Diagnostics avant le rendu final du jeu

`FrameCaptureDiagnosticContract` définit les vues explicites qui exigent un readback pré-final. `FilmicDisplayRenderer` lit leur sortie avant le repack et le shader final de Vintage Story, uniquement lorsque le G-buffer requis est disponible.

Pour les vues numériques, le fichier historique `*-vintagertx.png` consommé par le validateur contient maintenant le **canal du shader**, pas sa recomposition par le brouillard, le bloom ou la colorimétrie finale. Le rendu affiché reste conservé dans `*-postfinal.png`. Les pixels pré-final restent également disponibles dans `*-raw.png`.

Les comparaisons `Final` conservent leurs images A/B après le final du moteur. `ReflectionSource` et `EntityMirror` gardent leur contrat spécial de paire finale et témoin brut ; le témoin des entités peut être à une autre résolution.

Chaque paire reçoit un fichier `*-capture.json`, schéma 2, qui déclare la vue, le domaine, l’encodage et les dimensions. Le sandbox archive ces métadonnées par liste blanche. Ces PNG restent des visualisations RGBA8 avec les expositions diagnostiques existantes, **pas des mesures HDR non écrêtées**.

Une capture numérique sans pixels bruts, ou avec des dimensions différentes de sa paire, est recommencée. Aucune substitution par l’image finale n’est autorisée. Le séparateur du journal des paires brutes est aussi rendu compatible avec les lecteurs existants (`before and after, and raw pre-final ...`).

Conséquence importante : un ancien score de masque après composition finale n’est pas directement comparable à ce nouveau canal. Les seuils n’ont pas été assouplis. Le pipeline de mesure est corrigé pour révéler la contribution réellement produite ; il ne garantit pas que cette contribution soit déjà correcte ou suffisante.

### 2. Fin d’exécution distincte de la réussite

`RuntimeScenarioCompletion` attend le marqueur durable de fin des captures du bon profil et, lorsqu’il est demandé, le résultat A/B/A. Une assertion de matériau ou un token de nombre de lumières manquant ne maintient plus à lui seul un scénario terminé jusqu’au délai maximal.

**Toutes les validations existantes sont encore appliquées après cette fin d’exécution.** Un scénario terminé peut donc échouer immédiatement avec les mêmes causes réelles. La limite de temps n’est pas augmentée.

Les événements indépendants de mouvement et de redimensionnement restent requis. La chronologie particulière de `water-reflection`, comprenant des témoins physiques tardifs, conserve volontairement sa garde stricte existante ; ce lot ne prétend pas éliminer toutes les attentes possibles de ce scénario.

Le résultat distingue `EvidenceCollected`, `FatalRuntimeSignature`, `DeadlineReached` et `ExitedBeforeEvidence`. Un jeu sorti de lui-même trop tôt n’est plus systématiquement étiqueté comme un timeout.

### 3. Causes détaillées visibles dans MSTest

`RuntimeHarness.RunAsync` reste compatible avec les consommateurs CLI qui attendent un entier. `RunDetailedAsync` retourne un objet isolé par exécution, sans état statique de dernière erreur.

`InGameRealCaseTests` conserve l’assertion exigeant le code 0, mais son message inclut désormais la raison d’arrêt, **toutes les lignes FAIL originales** et le répertoire d’artefacts. `runtime-result.json` est joint au résultat MSTest/TRX ; une version texte est également écrite.

`runtime-inputs.json` contient les empreintes SHA-256 des cinq fichiers sélectionnés avant lancement : DLL du mod, modinfo et trois shaders. Ce n’est pas une attestation du module effectivement chargé ni du SHA Git local. Aucun `clientsettings.json`, jeton d’authentification ou fichier utilisateur arbitraire n’est copié.

## Qualification exécutée avant publication

Workflow `35512000653`, job `106081401409`, artefact `10605561886`, commit de publication **`eb7200dd0cebc3b8e2d668951ef4242469ede672`**.

Compilation Release du mod et du véritable projet MSTest contre le client officiel **Vintage Story 1.22.7**, SDK **.NET 10.0.401**, Ubuntu/Mesa/Xvfb : zéro erreur ; les deux avertissements préexistants du mod restent présents.

- Huit classes précédentes : **103 exécutés et réussis, aucun ignoré**.
- Six classes de capture/sandbox/résultat/buffer OpenGL : **33 exécutés et réussis, aucun ignoré**.

Ces ensembles sont disjoints : **136 tests ciblés**. Ils n’exécutent aucun des 21 mondes de la campagne utilisateur. Le filtre permanent `tests/reported-regressions.runsettings` sélectionne désormais ces quatorze classes complètes.

Les cinq nouveaux tests vérifient les quinze vues numériques via le service de capture réel et le décodage de vrais PNG de fixture : zéro préservé malgré un final non noir, RGB préservé, image finale séparée, refus de données absentes/dimensions incohérentes, distinction réussite/fin d’exécution, profils et événements indépendants, persistance des erreurs et empreintes sans paramètres client.

Trois anciens tests du terminal de capture appelaient directement `SavePair` sans fournir la nouvelle donnée brute obligatoire. Ils passent maintenant par la transaction baseline/raw/effect, sans supprimer leurs assertions sur le calendrier, le nombre de captures, la génération voxel ou l’ordre des notifications. Un test de paire finale utilise désormais explicitement `Final`, et le test de refus du brut utilise une vue finale plutôt qu’une vue `Material` devenue admissible. Aucune équation de shader, aucun preset et aucun seuil d’image ou de performance n’a été modifié dans ce lot.

La recette de publication a vérifié les empreintes des sources initiales, compilé/testé les fichiers finaux, puis publié sans force et seulement si la branche n’avait pas changé. Les fichiers de staging et le workflow d’écriture ponctuel ont été supprimés dans le commit de publication. Les workflows permanents restent en lecture seule.

## Non clos

Le transport indirect dynamique au niveau Performance, les budgets GPU/CPU réels, les défauts visuels propres aux scénarios et la qualification complète de l’eau restent ouverts. Une suite hors monde verte ne transforme pas les 19 échecs utilisateur en réussites.

Pour exploiter la campagne déjà effectuée sans la relancer, récupérer son TRX existant avec stdout/stderr, ou les journaux et captures de ses répertoires d’artefacts. Ne pas déduire une cause unique de la même ligne d’assertion. Préserver les deux réussites comme témoins de non-régression, sans les considérer comme une preuve que tous leurs masques étaient déjà exempts de contamination.
