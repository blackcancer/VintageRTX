# Matériaux réels, GGX et blocages de tests — 19 septembre 2026

Branche : `dev/renderer-recovery-20260918`.
Sources du présent lot : `641c4e4fea280dc964134348f2273031374ea68d`.
Base avant ce lot : `991e8f837763f5a7b78fa76d60e9c4387bfc5bf5`.
Les réparations de blocages de tests sont déjà présentes depuis `b3559ebf42c09559b3298dd2f6dc5009fff1cc9b`.

Ce lot ajoute du code de rendu utilisable, mais ne certifie ni la disparition de tous les défauts visuels ni un moteur RTX complet. Il ne modifie pas main et ne change aucune mécanique de lumière côté serveur.

## 1. Couleur non éclairée : nouvelle donnée, pas compensation de teinte

`RawAlbedoTarget` et `RawAlbedoCapture` capturent le RGB linéaire du texel réellement dessiné par les shaders opaques terrain et entités animées. Le terrain utilise sa couleur après colormap mais avant éclairage raster. Une cinquième sortie RGBA16F conserve RGB et profondeur Z de vue ; les quatre sorties natives ne sont pas réaffectées.

La capture commence avant le terrain, se termine avant la copie opaque et le rejeu des mains, et se détache avant consommation. Les programmes non identifiés comme producteurs n'ont pas accès à cette sortie. Un framebuffer étranger utilisant déjà cet emplacement n'est pas écrasé. Absence de layout compatible, erreur de hook ou d'OpenGL : seule cette capture optionnelle est abandonnée et le chemin de compatibilité demeure disponible.

Le consommateur exige des dimensions identiques, une surface compactée valide et une profondeur compatible. Les couleurs d'un objet au premier plan ne doivent pas être réutilisées sur la géométrie derrière lui. Quand la capture est valide, l'albedo ne dépend plus de la chromaticité de l'image déjà éclairée ou de la moyenne de couleur du voxel.

Le journal signale une première activation avec :

```text
[VintageRTX] Unlit RGB capture active: opaque attachment 4, paired view depth, ...
```

Cette ligne confirme la publication d'un buffer provenant d'un programme reconnu, pas que tous les shaders de tous les mods sont compatibles. Les renderers personnalisés, les objets utilisant d'autres shaders et certains effets procéduraux conservent des limites explicites. Le G-buffer doit être disponible. L'identification de shader utilise l'implémentation réelle du contrat public `IShaderProgram.Use`, pas un nom de classe privée supposé.

Coût ajouté : une texture RGBA16F à la définition du framebuffer et une sortie MRT pendant les dessins compatibles. Aucun gain de FPS n'est revendiqué. La capture ne rajoute pas un rejeu complet du monde et ne lit pas ses pixels vers le CPU pendant le rendu normal ; les contrôles d'état OpenGL restent à profiler.

## 2. Métaux : Fresnel évalué au bon moment

Le calcul spéculaire direct utilise maintenant le F0 du matériau avant l'évaluation GGX, avec visibilité Smith corrélée. Le soleil et les lampes passent par la même fonction. Les réglages de force solaire et d'émission sont appliqués à leur contribution respective.

Sont retirés : le Fresnel diélectrique blanc recoloré après coup, les piles de multiplicateurs métalliques, le plafond ponctuel arbitraire de BRDF, l'abaissement automatique de rugosité sur tous les métaux, et la transformation du cache diffus en projecteur spéculaire fictif. Le lobe diffus est nul pour une métallicité égale à un. La BRDF ponctuelle peut dépasser un ; c'est l'énergie intégrée du lobe testé qui doit rester bornée.

Les reflets ne reçoivent plus les gains successifs dépendant de la métallicité. La requête voxel suit la vraie direction miroir plutôt qu'un rayon décalé de côté par une phase de cône fixe. Ce dernier changement ne constitue PAS un intégrateur multi-directionnel de réflexion rugueuse GGX : il corrige une distorsion directionnelle et laisse la convolution physique complète ouverte.

La composition globale reste hybride : reliquat raster, ambiances héritées, cache indirect et heuristiques de confiance sont encore présents. Le test d'énergie du lobe spéculaire ne prouve donc pas que toute l'image finale conserve l'énergie.

## 3. Éclairage des points rencontrés dans les réflexions

L'albedo de l'impact, la lumière solaire et les couleurs des émetteurs sont convertis/accumulés en lumière linéaire. L'éclairage indirect utilise le champ local au lieu d'un terme constant d'albedo. Les huit sources sélectionnées peuvent contribuer, au lieu des deux premières seulement, avec leur visibilité.

La sortie de cette fonction est encodée une seule fois afin de préserver le contrat existant du transport de couleur des réflexions. Il n'y a plus de multiplication directe de deux couleurs encodées comme approximation d'un transport linéaire.

Les matériaux des impacts hors écran restent issus de la représentation voxel ; le nouvel albedo RGB écran ne rend pas magiquement exacts ces matériaux distants ou la géométrie animée absente. Le coût des rayons de visibilité supplémentaires doit être mesuré en jeu.

## 4. Normal maps

Le spéculaire des diélectriques rugueux n'est plus supprimé par un seuil d'éligibilité. La normale et la rugosité du matériau continuent d'alimenter le calcul direct ; la normale géométrique reste distincte pour les occultations.

Un test GPU exécute la vraie fonction de perturbation du shader terrain : taille d'UV normale, rectangle d'atlas minuscule et UV miroir. Il vérifie la direction résultante et la variation de réponse lumineuse. Cela qualifie ce calcul, pas l'ensemble des fichiers normal maps, des atlas multipages ou des renderers personnalisés.

## 5. Blocages du rapport utilisateur

La branche inclut la paire de projection miroir validée avant GL, le repli local sans désactiver tout le renderer, le démarrage/arrêt partiel, les contrôles de fins de lignes et les témoins de toit corrigés. Voir `2026-09-19-test-blockers.md` pour leur portée.

Le PNG de référence réel est maintenant embarqué dans l'assembly de tests, avec empreinte SHA-256 vérifiée avant décodage. Il reste également dans le dépôt. Le build échoue explicitement s'il manque ; aucune image synthétique de remplacement n'est générée. Les mêmes octets servent au test de chemin Unicode et de corruption.

Les assertions demandant les anciens gains métalliques ou le faux spéculaire du cache diffus ont été remplacées par les contrats de branchement du Fresnel et de l'albedo réels, accompagnés d'oracles numériques. Le bootstrap exige aussi l'enregistrement ET la libération du renderer supplémentaire. Aucun seuil d'image ni témoin visuel n'a été assoupli.

## 6. Vérifications réellement exécutées

Run GitHub Actions `35408638714`, job `105803544031` : les sources préparées depuis `7282e8be864dda9aed649d9c6fbc3dc56437425e` ont été compilées et testées avant publication en `641c4e4fea280dc964134348f2273031374ea68d`.

| Contrôle | Résultat |
|---|---|
| Build Release du mod et du projet MSTest contre le client officiel 1.22.7 | Réussite |
| Campagne partagée des blocages | 35 tests exécutés et réussis |
| Cible de capture réelle en contexte OpenGL caché | 1 test exécuté et réussi |
| GLSL de parcours et liaison du shader complet | 4 tests réussis |
| GLSL de visibilité, métadonnées et contacts diagonaux | 4 tests réussis |
| GLSL des matériaux | 6 tests réussis |
| Scénarios dans un monde Vintage Story | Non exécutés ici |
| Validation sur Windows et GPU de l'utilisateur | Non exécutée ici |

Total : 50 tests réussis dans ces suites ciblées, pas la totalité des tests du dépôt. SDK .NET 10.0.401, client Linux Vintage Story 1.22.7, Mesa llvmpipe et Xvfb/EGL.

Les six tests de matériaux vérifient 512 cas GGX contre une référence double précision à fonctions lambda de Smith, la teinte d'un F0 cuivre à trois rugosités, neuf intégrales de four blanc à 65 536 directions chacune, les cas géométriques dégénérés, le rejet d'albedo de mauvaise profondeur, et la fonction TBN de production. La tolérance du four blanc porte sur ce lobe spéculaire simple diffusion, pas sur la composition finale.

Le test C# de la cible MRT utilise sa vraie implémentation avec un petit shader témoin : conservation du RGB quand l'éclairage change, restauration des routes/couleurs/blend/scissor, absence de capture périmée et refus d'écraser un emplacement occupé. Il ne remplace pas une capture du vrai client avec ses renderers natifs.

## 7. Compilation et essais courts

Après sauvegarde du travail local et configuration de `VINTAGE_STORY` sur le client 1.22.7 :

```powershell
git fetch origin
git switch dev/renderer-recovery-20260918
git pull --ff-only
git log -1 --oneline

dotnet restore .\tests\VintageRTX.Test\VintageRTX.Test.csproj --source https://api.nuget.org/v3/index.json
dotnet test .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release --no-restore --settings .\tests\blocker-smoke.runsettings --logger "trx;LogFileName=blocker-smoke.trx"
dotnet test .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~RawAlbedoTargetTests" --logger "trx;LogFileName=raw-albedo.trx"
```

Ces commandes n'exécutent pas les scénarios longs en jeu. Le build du projet de tests compile aussi le mod. Redémarrer le client après remplacement des fichiers et éviter deux installations simultanées du mod.

Ensuite, une seule scène `lantern-night` ou une pièce fixe suffit pour le premier contrôle : cuivre et pierre avec normal map, une source portée puis posée, profil Quality sans adaptation et réglages inchangés. Contrôler la ligne d'activation RGB et les éventuels avertissements. Ne pas augmenter l'exposition pour compenser des gains métalliques retirés avant d'avoir comparé les matériaux.

## 8. Encore ouvert

- Réflexions rugueuses multi-directionnelles et sélection SSR/voxel cohérente sur toutes les discontinuités.
- Albedo exact pour toutes les voies d'objets/entités et matériaux des impacts hors écran.
- Attaches lumineuses animées exactes, mains indépendantes, masques mobiles de cage et corps animés pour les ombres locales.
- Reprojection temporelle en mouvement et filtrage propre à chaque signal ; l'ancien mélange RGB reste suspendu.
- Eau immergée complète, plusieurs plans de miroir et qualification des halos/rives sur le vrai client.
- Intégration du panneau directement dans les Graphismes natifs, au-delà du dialogue de style natif existant.
- Profilage CPU/GPU et scénarios réels complets ; pas de certification de performances.

Le workflow temporaire de publication est retiré après livraison. La CI en lecture seule exécute les fichiers C#/GLSL ordinaires du commit et garde ces contrôles. Les recettes de développement ne doivent pas être exécutées sur la branche déjà modifiée.
