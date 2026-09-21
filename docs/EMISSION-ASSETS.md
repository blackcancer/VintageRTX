# Émission configurable par assets — réécriture

Branche de développement unique : `dev/renderer-rewrite-20260921`.

Ce lot ajoute la configuration des comportements lumineux à l'observateur existant. Il conserve l'acquisition géométrique et les transferts régionaux introduits en parallèle. **Il ne produit pas encore une nouvelle image PBR dans le jeu.** Le vacillement décrit ici est évalué dans le `LightFrame` ; les futures passes directes, indirectes et réfléchies doivent consommer ce même état.

## Un vrai asset, traité par les patches natifs

Fichier livré :

```text
src/VintageRTX.Client/assets/vintagertx/config/emission.json
```

Adresse utilisée dans un patch : `vintagertx:config/emission.json`.

Le système de patches de Vintage Story modifie cet asset pendant `AssetsLoaded` ; le mod le lit ensuite à l'ordre 0.15, après le patcher natif 0.05. Il ne lit pas la copie sur disque, n'interprète pas les patches lui-même et ne réapplique pas les opérations une deuxième fois. Aucun framework de configuration externe n'est requis.

La méthode recommandée, y compris pour un pack **client uniquement en multijoueur**, consiste à patcher ce catalogue client : elle ne dépend pas d'une modification des définitions de blocs ou d'entités envoyées par le serveur.

Les profils et règles portent des clés nommées : pas d'indices de tableau fragiles à maintenir pour modifier un profil. Les chemins JSON Pointer peuvent utiliser le caractère `:` directement ; un éventuel `/` dans une clé doit être encodé `~1`, et `~` en `~0`.

## Exemple complet : bougie et chandelier

Dans un mod de contenu dépendant de `vintagertx`, créer :

```text
assets/monpack/patches/emission.json
```

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
    "value": "vintagertx:candle"
  }
]
```

Le premier changement réduit l'amplitude des bougies. Le second attribue ce profil aux chandeliers, placés ou représentés par un objet lâché. Pour une lumière sans vacillement ajouté, remplacer la valeur de `profile` par `vintagertx:steady`. Le dossier **`examples/emission-assets`** est un mod de contenu d'exemple complet ; il contient aussi l'ajout d'un profil et d'une règle pour un code fictif `example:crystal-lantern-*`. Il n'est pas déployé automatiquement avec VintageRTX.

Une modification de patch sur disque nécessite un redémarrage/rechargement complet des assets **avec réapplication des patches natifs**. Le rechargement des seules textures ou un appel isolé à `IAssetManager.Reload` ne remplace pas cette étape. Aucune commande de « hot reload » trompeuse n'est fournie.

## Profils et règles

Un document comporte `schemaVersion: 1`, un objet `profiles` et un objet `bindings`.

| Champ du profil | Sens et limites |
|---|---|
| `kind` | `steady`, `flame`, `engineDriven` ou `lightning` ; casse exacte |
| `amplitude` | Amplitude relative du vacillement, de 0 à 0.8 ; valeur par défaut 0 |
| `frequencyHz` | Fréquence de base du bruit temporel, de 0.000001 à 100 Hz ; défaut 1 |
| `windSensitivity` | Sensibilité de l'amplitude au vent normalisé, de 0 à 1 ; défaut 0 |
| `durationSeconds` | Durée de l'enveloppe de foudre, de 0.000001 à 60 s ; défaut 0.4 |

Le profil `steady` n'ajoute aucune fluctuation, mais une extinction ou variation d'émission fournie par le jeu reste effective. `engineDriven` préserve également l'enveloppe moteur sans bruit supplémentaire ; il explicite son origine. Les paramètres des flammes sont artistiques et bornés, pas des spectres mesurés. La couleur reste celle du fournisseur runtime : aucune correction orange/bleue ou température imposée par le profil.

`lightning` est réservé à une règle `weather` et demande au futur fournisseur météo une date de naissance de l'événement. Il est rejeté sur un bloc, un objet ou une entité persistante : découvrir un objet n'est pas une preuve qu'un éclair vient de se produire. Le fournisseur météo réel reste à implémenter.

| Champ de règle | Sens |
|---|---|
| `codes` | 1 à 32 codes `domaine:chemin` en minuscules ; `*` est le seul joker |
| `targets` | 1 à 32 valeurs distinctes parmi `block`, `item`, `entity`, `weather` |
| `profile` | Identifiant qualifié d'un profil existant |
| `priority` | Entier de -10000 à 10000, défaut 0 |
| `enabled` | Autorise la contribution VintageRTX ; défaut true, ne rallume jamais le jeu |
| `intensityScale` | Multiplicateur relatif de 0 à 100, défaut 1 ; **pas une valeur en watts, lux ou candelas** |

Une règle de priorité supérieure gagne. À priorité égale, le code exact gagne sur un joker, puis le motif ayant le plus de caractères littéraux gagne. Des règles contradictoires de même rang pour un code sont signalées au lieu d'être sélectionnées selon leur ordre d'énumération. Utiliser des priorités distinctes pour résoudre une vraie concurrence. Deux règles équivalentes ne créent pas deux sources.

Sans règle : source de bloc/objet stable, source intrinsèque d'entité ou météo pilotée par le moteur. La teinte ou un mot dans le nom d'un mod n'est jamais une preuve de flamme.

## Ajouter une source d'un autre mod

```json
[
  {
    "file": "vintagertx:config/emission.json",
    "side": "client",
    "op": "add",
    "path": "/profiles/monmod:petite-flamme",
    "value": { "kind": "flame", "amplitude": 0.09, "frequencyHz": 5.5, "windSensitivity": 0.05 }
  },
  {
    "file": "vintagertx:config/emission.json",
    "side": "client",
    "op": "add",
    "path": "/bindings/monmod:lampe",
    "value": {
      "codes": ["monmod:lampe-*"],
      "targets": ["block", "item"],
      "profile": "monmod:petite-flamme",
      "priority": 100,
      "enabled": true,
      "intensityScale": 1.0
    }
  }
]
```

Le code doit être le **code runtime de l'objet**, variantes incluses, pas le nom de son fichier de définition.

## Attribut optionnel pour les auteurs de contenu

Un type de bloc, d'objet ou d'entité peut aussi exposer :

```json
"attributes": {
  "vintageRtxEmission": {
    "profile": "vintagertx:candle",
    "enabled": true,
    "intensityScale": 1.0
  }
}
```

Les trois membres sont facultatifs ; ceux présents remplacent la sélection du catalogue. `enabled:false` seul est permis. Le parseur rejette les fautes de noms, les références inconnues et les valeurs non finies. L'attribut de **type** est utilisé, pas une chaîne arbitraire contenue dans un ItemStack. L'état réel du stack, du block entity ou de l'entité continue de déterminer son émission via `GetLightHsv`/`LightHsv`.

Un patch natif peut ajouter `/attributes/vintageRtxEmission` lorsque `/attributes` existe. Si ce parent n'existe pas, l'auteur du mod de contenu doit d'abord le créer sans écraser d'autres attributs. Pour les types envoyés par le serveur, l'attribut doit appartenir à la définition effectivement reçue ; ne pas supposer qu'un patch client de `blocktypes/` survivra à la réception des données serveur. Le catalogue client ci-dessus évite cette dépendance.

## Bougies, chandeliers, lanternes et mains

Le catalogue livré couvre feu/torche, lanterne, lampe à huile, bougies et regroupements de bougies, chandeliers et locustes. Les bougies et chandeliers ont des profils nommés séparés. Le chandelier reste pour ce lot **une source agrégée** : son `LightHsv` porte déjà l'effet du nombre de bougies ; ce nombre n'est pas multiplié une seconde fois. Un chandelier sans émission runtime ne reçoit aucune énergie, même si son profil est une flamme.

La localisation des différentes mèches, leur vacillement individuel, leurs ombres et les attaches animées exactes ne sont pas encore implémentés. L'objet lâché utilise le profil de son vrai collectible. Le joueur conserve provisoirement son agrégat `EntityPlayer.LightHsv` sans superposer une copie de chaque main ; le profil propre à une bougie **tenue** n'est donc pas encore évalué séparément. Les ancres restent provisoires.

La foudre et les locustes ne reçoivent pas de bruit de flamme par défaut. Les locustes restent soumis à leur émission moteur, y compris leurs variantes éteintes et leurs transitions d'agressivité.

## Validité, coût et publication

Le catalogue est immuable après parsing. Un candidat invalide ne remplace pas la version précédente. Au tout premier démarrage sans catalogue valide, le journal l'indique et la politique vide reste stable/engine-driven, sans prétendre avoir chargé les profils fournis.

Les règles et attributs sont résolus et mis en cache par type/cible et token d'attribut, pas reparsés à chaque image. Une erreur d'attribut est mémorisée et désactive seulement cette contribution VintageRTX. Une nouvelle révision invalide ces résolutions ; les observations existantes sont réévaluées sur le thread propriétaire avant la capture du `LightFrame`, sans rescan du monde ni nouvelle identité de source. Les anciens frames restent immuables.

La surveillance des émetteurs potentiels continue lorsque leur état devient éteint, afin de pouvoir observer un rallumage sans remplacement du bloc. Sa liste n'est reconstruite que lorsque ses membres changent. Le polling d'émission seule ne retesselle plus le bloc ; les invalidations géométriques continuent de passer par les événements bloc/chunk et le rechargement des assets de forme/texture. Ces réductions de travail ne sont pas encore des mesures de gain FPS.

Commande de diagnostic : **`.vrtxemissions`** affiche le chemin du catalogue, ses nombres de profils/règles, sa révision, son SHA-256 et ses erreurs. `.vrtxrewrite` conserve les compteurs de régions et transferts GPU.

## Qualification

Les tests ajoutés sont dans `EmissionCatalogTests` (cœur indépendant du jeu) et `EmissionAssetTests` (DLL du client officiel). La seconde suite exécute le vrai `ModJsonPatchLoader` sur des assets en mémoire et les patches fournis dans l'exemple, puis charge les octets effectivement modifiés avec le chargeur de production. Elle vérifie aussi la priorité des attributs, la conservation du catalogue après erreur, le cache et les familles bougie/chandelier dans les assets officiels.

Les tests de maillage, de textures GPU et de traversal de la branche sont conservés. Le succès de ces tests ne remplace pas une validation des pixels/performances en jeu : les nouvelles passes PBR d'image ne sont pas encore actives.

Références :
- https://wiki.vintagestory.at/Modding:JSON_Patching
- https://apidocs.vintagestory.at/api/Vintagestory.API.Common.ModSystem.html
- https://github.com/anegostudios/vsessentialsmod/blob/06673b67318e002332b6aa8b3d0308f11d2aba99/Loading/JsonPatchLoader.cs
- https://github.com/anegostudios/vssurvivalmod/blob/849fa8cad9e392368566efc7474e73db6404a145/Block/BlockChandelier.cs
