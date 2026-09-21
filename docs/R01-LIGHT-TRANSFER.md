# R01b — émissions configurables et publication lumineuse GPU

Branche unique : `dev/renderer-rewrite-20260921`. Ce lot poursuit les patches de bougies/chandeliers et raccorde les sources évaluées à un transfert GPU indépendant de la géométrie. **Il ne remplace pas encore l'image native du jeu.**

## Configuration par patches natifs

L'asset final est `vintagertx:config/emission.json`. Le mod le lit à `AssetsLoaded`, après le patcher natif, via `IAssetManager`, jamais en relisant le fichier original sur disque. Profils et règles portent des clés nommées pour éviter les indices fragiles. Les attributs de type `vintageRtxEmission` sont des overrides facultatifs. La documentation complète et le mod d'exemple sont dans `docs/EMISSION-ASSETS.md` et `examples/emission-assets`.

Exemple dans `assets/monpack/patches/emission.json` :

```json
[
  {
    "file": "vintagertx:config/emission.json",
    "side": "client",
    "op": "replace",
    "path": "/profiles/vintagertx:candle/amplitude",
    "value": 0.08
  },
  {
    "file": "vintagertx:config/emission.json",
    "side": "client",
    "op": "replace",
    "path": "/bindings/vintagertx:chandelier/profile",
    "value": "vintagertx:steady"
  }
]
```

Ici les bougies conservent une petite flamme fluctuante et les chandeliers n'ajoutent aucun vacillement. `steady` ne signifie pas toujours allumé : l'état runtime conserve l'autorité sur l'extinction. Les règles ciblent séparément `block`, `item`, `entity` et `weather`. Pour les packs client en multijoueur, patcher ce catalogue client évite de dépendre de définitions serveur remplacées à la connexion.

Le chandelier reste agrégé. L'énergie fournie par son `LightHsv` n'est pas multipliée une seconde fois par le nombre de mèches. Le placement précis et le vacillement individuel des mèches sont ouverts. Les mains du joueur utilisent encore l'agrégat moteur et n'appliquent pas individuellement le profil du collectible tenu.

## Chemin de publication

`Observation → EmissionSelection → LightRegistry → LightFrame → GpuLightData → LightTexture`.

Le `LightFrame` contient les valeurs finales d'une image. Le GPU ne possède ni horloge de flamme, ni tirage aléatoire supplémentaire. Toutes les passes doivent réutiliser ces valeurs. Modifier l'émission seule ne reconstruit pas la géométrie.

`GpuLightData` conserve deux buffers CPU réutilisables. Une ligne contient deux texels RGBA32F : position relative à une ancre entière/rayon, puis RGB linéaire d'intensité/zéro. Les différences de coordonnées sont calculées en double avant conversion en float. Aucun écrêtage à un ni conversion sRGB supplémentaire n'est appliqué. La capacité croît géométriquement ; les emplacements retirés sont remis à zéro. Les frames anciennes ou conflictuelles d'un même monde sont rejetées, et un candidat invalide ne remplace pas les données précédemment validées.

`LightTexture` est privée au mod. Elle préserve l'unité et la texture actives, le PBO d'unpack et les paramètres d'unpack, y compris l'inversion d'octets. Elle ne touche pas au framebuffer ni au programme de rendu. Le frame publié n'est exposé qu'après transfert réussi ; une erreur retire immédiatement sa disponibilité. Les données ne sont pas relues du GPU en production.

Une source stable peut faire avancer le numéro de frame sans aucun nouvel upload si les pixels et l'ancre restent identiques. Une flamme modifie ses pixels sans toucher aux maillages. La texture n'impose pas huit sources : un dépassement de capacité matérielle est signalé plutôt que tronqué silencieusement. Cela n'est pas une promesse de coût constant pour un nombre illimité de lumières.

L'observateur publie l'émission en `Before`, avant son upload géométrique. La découverte initiale des blocs conserve ses budgets progressifs et l'émission des blocs est encore scrutée sur les ticks client : ce lot ne prouve pas une latence événement→pixel finale.

## Requêtes GPU de référence

`light-query.glsl` utilise le même `sceneAnchor` que `scene-query.glsl`. Son évaluateur ponctuel transporte le RGB selon la distance au carré puis consulte la visibilité géométrique. L'évaluateur diffus applique le facteur lambertien et distingue les normales de surface et géométrique. Les entrées doivent être des normales normalisées et un albedo linéaire valide.

Un résultat `Unknown`, `Unsupported` ou `BudgetExhausted` n'est jamais un rayon libre. Les contributions non résolues sont comptées séparément. Un rayon de source non nul est refusé par cet évaluateur ponctuel : il faudra le vrai estimateur surfacique, pas une conversion silencieuse en point. Le minimum du rayon est fourni explicitement par le récepteur ; aucune exemption générale des lampes tenues ou des blocs émetteurs n'est introduite.

Le premier test intégré utilise des maillages et lumières produits en C#, chargés avec les deux uploaders réels puis lus dans le shader réel. Huit sources sont derrière le récepteur, la neuvième est une bougie configurée. Le calcul attendu est indépendant du shader : `albedo * intensité_evaluée / (pi * distance²)`. Le test ajoute un obstacle, remplace la cellule par une région inconnue puis éteint la bougie. Il ne constitue pas encore une image du jeu ni un chemin de réflexion complet.

## Régressions réparées pendant la reprise

- Le chemin d'échange des paquets C#→Python était relatif au répertoire de travail du test host. La CI utilise maintenant un chemin absolu commun, supprime l'ancien fichier avant la campagne et vérifie les six groupes avant les tests GLSL.
- La division/modulo GLSL utilisait `%` avec des opérandes négatifs. La nouvelle formulation ne soumet au modulo que des entiers non négatifs et conserve `INT_MIN` sans `abs(INT_MIN)` ni conversion float. Test sur 1 545 valeurs signées, dont les extrêmes int32.
- `CollectibleObject.LightHsv` est un `ThreeBytes`, pas un tableau doté de `Length` dans l'API cible. L'observateur lit directement son troisième composant pour le marquage d'émetteur potentiel.
- Les callbacks de test qualifient `System.Func`, et leurs assertions OpenGL utilisent un alias d'`ErrorCode` distinct de GLFW. Aucun critère de test n'est retiré.

## Validation et limites

Les résultats exacts de compilation et d'exécution doivent être lus dans le run du commit correspondant. Les tests portent sur le parsing et les vrais patches natifs, les maillages, les uploads, les états OpenGL, l'extinction, le changement de monde, le RGB HDR et les coordonnées négatives. Les tests sur Mesa ne remplacent pas les essais Windows en jeu. Le client officiel reste 1.22.7 avec SDK 10.0.401 ; aucune DLL du jeu n'est publiée avec les preuves.

La suite de R01 reste la capture complète des surfaces/matériaux et leur rendu natif correctement raccordé, avec un contrat de même monde/dimension/ancre pour tous les buffers consommés. La sélection spatiale des lumières, les reflets, la GI et les émetteurs surfaciques demeurent dans le périmètre, pas supprimés pour réduire les budgets.
