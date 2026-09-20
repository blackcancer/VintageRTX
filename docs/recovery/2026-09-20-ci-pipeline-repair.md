# Réparation des deux chemins CI et du laboratoire multipasse

Date : 20 septembre 2026. Branche : `dev/renderer-recovery-20260918`.

Code publié après vérification : **`b53c61b7a127e022030f97cfb708b90545b6e362`**.
Ce lot corrige le préflight et le laboratoire autonome, pas les équations des shaders ni les défauts encore ouverts des 21 scénarios dans Vintage Story.

## Échecs signalés et reproduits

| Run fourni | Révision | Cause constatée |
|---|---|---|
| Renderer recovery `35527420252` | `d1514365` | `PreflightContract (display shader contracts)` exige encore le modulo temporel de rebond retiré du moteur. 34 tests réussissent, un échoue. |
| Installable recovery delivery `35527420423` | `d1514365` | Compilation, 155 régressions et 27 tests GLSL réussis ; `ProductionShaderCompilesAndRendersHeadlessly` dépasse ses 120 secondes. L'empaquetage est bien bloqué. |
| Renderer recovery `35527907470` | `338ea61b` | Même assertion obsolète du préflight ; pas une nouvelle erreur de compilation C# ou GLSL. |

Les notifications historiques restent des échecs des révisions indiquées. Un nouveau commit et de nouveaux runs ne changent pas rétroactivement leurs résultats. Les anciens rapports locaux « Périmé » ne sont pas utilisés pour attribuer une cause différente à ces trois runs.

## Préflight : remplacer les anciennes exigences, pas retirer leur objectif

Trois assertions imposaient l'ancien système : modulo du numéro de frame, clause de cadence dans le shader et extinction du rebond au niveau Performance au profit du cache statique. Elles étaient incompatibles avec le transport diffus courant à résolution réduite.

Elles contrôlent maintenant le raccordement des canaux HDR brut/filtré/résolu, l'absence de modulo produisant une alternance radiance/zéro et la liaison au budget `DiffuseTransportBudget`. Les trois niveaux sont aussi testés directement : contribution positive avec un rayon demandé, zéro avec une force explicitement nulle. Les contrôles du cache de ciel, des sources indépendantes, des réflexions et des occultations restent présents.

Les essais GLSL existants continuent notamment de vérifier une source chaude dans le huitième emplacement, la préservation HDR et l'extinction sans reprise de radiance périmée. Aucun seuil d'image, de matériau, de performance ou délai n'est relevé.

## Laboratoire : utiliser le véritable chemin réduit

Le laboratoire autonome appelait une seule fois le shader de composition, sans produire ses textures de visibilité et de transport réduit. Il exerçait donc le repli pleine résolution, alors que le renderer du jeu utilise déjà trois passes.

`StandaloneTransportPipeline` réalise maintenant, sur les mêmes données synthétiques et avec le shader de production inchangé :

1. visibilité par source et rebond diffus courant à demi-résolution ;
2. filtre spatial guidé par la géométrie ;
3. reconstruction et composition à pleine résolution.

Deux banques de trois textures RGBA16F sont distinctes de la cible finale. Le canal solaire et le RGB du rebond conservent leur séparation. Le shader conserve ses sources, ses distances, ses paramètres de matériaux et son budget angulaire. Le budget de parcours diffus est déduit de la distance de six blocs comme dans le contrat de production.

Les diagnostics, les 30 frames de chauffe et les 60 frames mesurées passent tous par la même chaîne. Le timer GPU entoure les **trois** dessins, pas seulement la dernière composition. Le test compte 294 dessins : huit captures, 30 frames de chauffe et 60 frames mesurées, chacun avec trois passes.

Ce laboratoire garde sa scène synthétique ; ses images ne constituent pas une réception des mondes en jeu. Son historique de visibilité est désactivé pour ce test déterministe, et les fonctions temporelles restent qualifiées par leurs essais GLSL séparés.

## Liaisons et propriété des textures

Lors de l'allocation de la cible finale, l'unité 15 était encore active après la création de la simulation liquide. La nouvelle cible remplaçait alors son binding : `dynamicLiquidSurface` pouvait lire le framebuffer en cours d'écriture au lieu de la texture de simulation. Ce n'était pas une mesure valide de cette entrée.

Le binding de la simulation est maintenant conservé. Avant chaque dessin du laboratoire, le contrôle de propriété rejette une cible d'écriture également liée pour l'échantillonnage. Les banques alternent sans alias, y compris pour les samplers de branches inactives. Les profondeurs de comparaison et les profondeurs ordinaires disposent aussi d'unités distinctes valides ; les cascades absentes de la scène synthétique restent désactivées.

Les matrices de vue et de projection inverse, auparavant non liées dans le laboratoire, sont explicitement produites depuis la même caméra. Aucun changement de ces matrices dans le renderer du jeu n'est introduit par ce lot.

Deux nouveaux tests OpenGL vérifient l'identité et le format du véritable binding liquide, injectent un alias interdit pour exiger un refus, puis exécutent les passes brutes/filtrées/finales sur des cibles HDR disjointes avec restauration du viewport complet.

## Qualification avant publication

Run **`35537746930`**, job **`106149705841`**, artefact **`10613024809`** ; `published-commit.txt` identifie `b53c61b7`.

Client officiel **Vintage Story 1.22.7**, SDK **.NET 10.0.401**, Ubuntu 24.04 / Mesa llvmpipe / Xvfb. Compilation Release du mod et des deux projets de tests : zéro erreur. Les deux avertissements préexistants du mod (nullabilité dans `RuntimeScenarioProbe`, commentaire XML dans `FilmicDisplayRenderer`) restent présents.

| Campagne exécutée | Résultat |
|---|---|
| `blocker-smoke.runsettings` | 35 réussites, aucun ignoré |
| `reported-regressions.runsettings` | 155 réussites, aucun ignoré |
| `StandalonePipelineTests` | 2 réussites, aucun ignoré |
| `ProductionShaderCompilesAndRendersHeadlessly` | 1 réussite en **108,733 s**, délai conservé à 120 s |
| Quatre suites GLSL (parcours, retours de jeu, matériaux, transport) | 27 réussites |

Les sélections C# comportent deux tests communs. Les 193 exécutions correspondent à 191 identifiants de tests distincts ; cela n'est ni une couverture totale du dépôt, ni un résultat des 21 scénarios en jeu.

Le test RenderLab utilise un répertoire de cache Mesa neuf : les deux petits tests de pipeline ne préchauffent pas artificiellement ce cache. Les critères existants, les **640 × 360 pixels**, les **30** frames de chauffe et les **60** frames mesurées sont conservés. Les assertions supplémentaires exigent le nombre de frames et l'existence des mesures de phases.

Mesures du run de qualification (pas des performances du GPU Windows du joueur) :

| Phase | Temps |
|---|---:|
| Exercice des services GPU de production | 71,574 s |
| Construction de la scène synthétique | 0,376 s |
| Initialisation GL du laboratoire | 0,213 s |
| Captures, première utilisation et analyses | 30,136 s |
| 30 frames de chauffe | 2,122 s |
| 60 frames mesurées, toutes passes | 4,202 s |

La phase des services GPU comprend encore plusieurs opérations ; le présent lot ne prétend pas isoler compilation pilote et dessins internes à cette phase. Le passage sous 120 secondes n'établit pas une marge universelle sur tout runner. Les mesures sont maintenant dans stdout/TRX et écrites dans le dossier propre au test pour diagnostiquer une régression ultérieure.

## CI permanente et livraison

Le workflow de livraison ajoute les mêmes 35 contrats de préflight que `Renderer recovery`, ainsi que les deux contrôles du pipeline autonome. La lecture des rapports TRX refuse les tests absents, échoués, ignorés ou inconclusifs. Le RenderLab complet y utilise également un cache Mesa neuf. Le minimum de la campagne transport GLSL est actualisé à ses dix tests, pas laissé à cinq.

Le manifeste et le fichier d'installation décrivent les corrections déjà présentes sans déclarer à tort que le rebond dynamique ou les diagnostics pré-final sont encore absents. Ils conservent explicitement l'absence de validation en jeu et les défauts visuels et budgets ouverts.

La publication ponctuelle a vérifié les identités des sources, compilé et testé les fichiers finaux avant un push sans force. Ses fichiers de staging et son workflow d'écriture sont retirés. Les workflows permanents restent en lecture seule. Leurs résultats doivent être consultés sur le commit qui les déclenche, indépendamment de ce compte rendu de qualification.
