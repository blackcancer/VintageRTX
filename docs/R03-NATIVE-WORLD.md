# R03 — raccordement aux surfaces natives du monde

Branche conservée : `dev/renderer-rewrite-20260921`. Base du travail : `0fe6f790e2d0acb6163fb03672ebce634feed56f`. Version du paquet : `0.3.1-dev.1` ; client ciblé : **Vintage Story 1.22.7**.

## Ce qui est raccordé

Le shader du monde appelle désormais le même `evaluateMaterialDirect` que `DirectImagePass`, directement pendant la rastérisation des primitives du jeu. Il ne construit pas un deuxième monde à partir de surfaces synthétiques et ne récupère pas l'albédo depuis le framebuffer éclairé.

| Programme natif | Données conservées et raccordées |
|---|---|
| `chunkopaque` | Position et normale natives, texture d'atlas et coloration climatique avant l'éclairage. |
| `chunktopsoil` | Même principe, avec mélange réel du sol et de l'herbe et leurs deux UV. |
| `entityanimated` | Texture, teinte et géométrie animée natives comme récepteurs. |
| `standard` | Objets au sol et maillages monde utilisant ce programme ; teinte et overlay natifs conservés. |

Le raccordement porte actuellement sur **l'éclairage diffus des sources locales**, ponctuelles ou sphériques selon les assets. Les entités qui fournissent leur émission via l'observateur partagent la même `LightFrame` que les sources posées. La modulation n'est pas recalculée dans le shader. La disponibilité du paquet lumineux ne dépend toujours pas d'une reconstruction générale du monde.

`WorldLightingRenderer` intervient avant le terrain à l'ordre opaque 0,36 et ferme sa liaison à 0,79, avant les dessins des mains décrits par l'API à partir de 0,8. Les variantes de profondeur première personne et OIT de l'entité ne passent pas par le remplacement ; `standard` conserve aussi son chemin natif `GLOWSUB`. Les GUI/overlays après cet intervalle retrouvent le mode natif. Cette frontière doit encore être réceptionnée avec les renderers tiers dans un vrai monde.

Les shaders sont modifiés **en mémoire**, jamais dans l'installation du jeu. Les huit stages sont validés avant modification. `IShaderAPI.ReloadShaders()` recompile les programmes du client. Contrat inattendu, compilation rejetée ou exception de rechargement : restauration des assets possédés et tentative de rechargement natif, erreur explicite dans le statut. Une restauration qui échoue elle aussi n'est pas présentée comme réussie.

## Corrections bloquantes et cohérence

La base contenait un identifiant GLSL `sample`, réservé dans le profil utilisé par la variante SSBO. Il est remplacé par `emitterSample` dans les deux fonctions de transport concernées. Les programmes natifs GLSL 330 et 430 sont compilés avec les includes officiels dans les tests.

Le RGB de lumière précalculée du bloc n'est pas ajouté à l'éclairage tracé. Le transport se fait en RGB linéaire, puis est encodé une seule fois dans le porteur couleur attendu par les opérations natives de brouillard, sous-eau et composition finale. L'environnement conserve la composante de ciel native ; le plancher lié aux lampes n'est pas réutilisé comme preuve de visibilité solaire. Ce n'est pas encore un nouveau modèle physique du soleil/ciel ni une exposition photographique calibrée.

La référence monde est `ShaderUniforms.playerReferencePos`, en double précision. L'ancre entière est soustraite avant conversion en float. Les tests vérifient les mêmes pixels aux origines nulle, négative et ±1 milliard de blocs.

Un objet dont la forme n'a pas encore de fournisseur d'occultation peut être un **récepteur valide** : sa surface est effectivement dessinée par le moteur. Le shader ne l'exclut donc plus simplement parce que son bloc est étiqueté `Unsupported`. Cela ne rend pas sa géométrie transparente pour les rayons : tout segment entrant dans une cellule non prise en charge reste non résolu. Aucune exemption de la cellule du récepteur ou de l'émetteur n'a été ajoutée.

Quand une lampe proche est résolue et qu'une autre source quitte la zone connue, la première contribution reste publiée immédiatement. Les échantillons inconnus n'ajoutent aucune énergie et restent dans le dénominateur : **estimation partielle inférieure**, identifiée dans le diagnostic, et non visibilité prétendument mesurée. Si aucun segment n'est résolu, le pixel conserve le rendu natif. Une frame sans source ne conserve pas une ancienne énergie ; aucune histoire temporelle n'est utilisée par ce raccordement.

## Commandes et démarrage

Le raccordement est demandé **par défaut** au premier rendu d'un monde, une fois les assets et le contexte graphique disponibles. Il ne faut pas activer le laboratoire pour le voir.

```text
.vrtxworld status
.vrtxworld on
.vrtxworld off
.vrtxworld coverage
.vrtxworld retry
.vrtxrewrite
```

`status` indique installation des stages, nombre de frames réellement liées, numéro de frame lumineuse, attente ou dernière erreur. Un compteur de frames liées confirme l'alimentation des programmes, pas un nombre de pixels remplacés : utiliser `coverage` pour cela.

Dans `coverage` : vert = transport résolu ; bleu = au moins un segment occulté ; cyan = contributions résolues avec une partie encore inconnue ; magenta = transport entièrement non résolu ; orange = cellule du récepteur non observée. Les surfaces exclues (glow, matériaux réfléchissants natifs, variantes protégées) restent natives. Ce ne sont pas des faux objets colorés ajoutés au monde.

`off` garde les programmes patchés mais désactive le remplacement. Les unités entières et flottantes des samplers restent distinctes même dans ce mode pour éviter une erreur de validation GL. Les bindings de textures/samplers et le programme actif sont restaurés à la fin de l'intervalle. `retry` retente explicitement l'installation après une erreur, sans boucle de recompilation à chaque frame.

Le paquet contient seulement `VintageRTX.dll`, `VintageRTX.Core.dll`, `modinfo.json` et `assets/`. Ne pas charger simultanément recovery, rewrite et une deuxième copie installée du même mod. Les profils F5 existants, les chemins Debug/Release et le projet de démarrage restent inchangés.

## Qualification exécutable

Tests `NativeWorldLightingTests` et `WorldShaderAssetTests`, en plus des campagnes préexistantes : douze variantes natives, chaîne d'événements enregistrée, échec/exception et reprise du reload, quatre chemins d'albédo, lumière chaude, indépendance au RGB précalculé, extinction immédiate, triangle occultant, zone inconnue, contribution partielle et coordonnées extrêmes. Les vrais uploaders et la traversée régionale sont exécutés dans un contexte GL caché.

Le workflow `rewrite.yml` exige ces classes et produit `artifacts/native-world/native-world-pixels.json`. Ce fichier contient les valeurs natives, éclairées, avec lumière précalculée modifiée et après extinction pour les quatre programmes. Il précise sa portée : **maillage texturé contrôlé rendu par les vrais shaders officiels, pas capture d'un monde joué**. Le laboratoire historique reste également qualifié.

Résultats locaux avant push : 203 tests cœur et 72 tests client réussis sans ignoré, références officielles 1.22.7, SDK 10.0.401. Pour la révision publiée, consulter ses propres runs GitHub : cette note ne prédit pas leur résultat. Le seuil de couverture 100 % n'a pas été abaissé ; ce lot ajoute des chemins et ne revendique pas le seuil atteint. Aucune mesure FPS en jeu n'a été effectuée.

## Limites qui restent visibles en jeu

Le cache couvre un voisinage de 3 × 3 × 3 régions de 8 blocs : les rayons hors de ce voisinage ne sont pas certifiés. Son fournisseur actuel accepte seulement certains maillages opaques statiques. Les cages de lanternes, flammes découpées par alpha, chandeliers, géométries contextuelles et animées ne sont **pas** tous des occultants pris en charge. En particulier, une source placée dans une cellule non prise en charge peut conserver l'éclairage natif quand son segment terminal ne peut pas être certifié. Ce n'est pas corrigé en ignorant arbitrairement sa cellule.

Les entités sont raccordées comme **récepteurs** et comme sources déjà observées, pas comme nouveaux occultants animés. La position de l'émission agrégée et les attaches exactes des mains/mèches restent à qualifier ; les chandeliers gardent leur quantité dynamique mais pas leurs ombres individuelles.

Le shader diffus ne remplace pas les surfaces marquées réfléchissantes ou intrinsèquement lumineuses. Les normal maps/PBR complets du monde, les conducteurs GGX en situation réelle, les réflexions secondaires/hors écran, la réfraction/eau et la GI restent les raccordements suivants. Le fonctionnement de `DirectImagePass` sur des conducteurs en laboratoire ne les rend pas implicitement disponibles dans le monde.

Références officielles :
- https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderer.html
- https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IShaderAPI.html
- shaders et includes du client officiel 1.22.7 utilisés par les tests de la branche.
