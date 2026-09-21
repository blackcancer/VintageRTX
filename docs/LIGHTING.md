# Comportement des sources

## Trois concepts distincts

Le propriétaire donne l'état actif, la couleur et l'intensité de base. Le profil temporel donne une modulation. Le transport géométrique donne la visibilité et les rebonds. Un changement d'intensité ne change pas la topologie ; un déplacement de flamme ou de cage changerait bien la visibilité.

| Famille | Profil de la nouvelle base | Limites actuelles |
|---|---|---|
| Feu, torche | Bruit lissé multibande, déterministe par source et temps, intensité positive, réaction au vent bornée | Les paramètres sont artistiques et réglables par construction, pas des mesures physiques ; pas encore de couplage aux particules |
| Lanterne | Même mécanisme, fluctuations réduites et très faible couplage au vent | La protection de la flamme ne supprime pas l'ombre de la cage ; attache géométrique à développer |
| Lampe à huile | Profil intermédiaire adapté à une petite flamme | Famille par code, runtime actif fait autorité |
| Locuste et source intrinsèque | Émission fournie par l'entité, aucune fluctuation de flamme ajoutée | Respecte les modifications liées à l'agressivité ; ancres encore approximatives dans l'observateur |
| Foudre | Événement fini avec enveloppe dédiée, ou enveloppe exacte fournie par le moteur | Modèle de référence en place, raccord météo réel non implémenté |
| Autre mod | Stable par défaut | Contrat d'extension de profils à exposer ; ne pas deviner une flamme depuis une couleur orange |

Le modèle de foudre de référence est un double flash fini, non périodique. Le vrai backend devra consommer l'événement local rendu et éviter de rejouer l'effet natif en double. Il ne faut pas utiliser un événement serveur d'incendie comme s'il s'agissait de la durée lumineuse de l'éclair.

`EntityLocust` hérite de `EntityGlowingAgent` : la variante sawblade peut ne pas émettre, et l'émission peut être conditionnée par l'agressivité. La réécriture lit l'état réel, elle n'allume pas systématiquement tous les locustes. Sources inspectées :
https://github.com/anegostudios/vssurvivalmod/blob/849fa8cad9e392368566efc7474e73db6404a145/Entities/EntityLocust.cs
https://github.com/anegostudios/vssurvivalmod/blob/849fa8cad9e392368566efc7474e73db6404a145/Entities/EntityGlowingAgent.cs

La conversion de `GetLightHsv` conserve la chromaticité runtime. Le niveau de jeu n'est pas une mesure de watts/candela : le premier adaptateur emploie une calibration relative déclarée. Le RGB n'est ni saturé ni réchauffé artificiellement. Les futures valeurs photométriques devront conserver leur provenance et leurs unités.

## Cohérence temporelle

`LightFrame` est figé une seule fois avant les consommateurs. Une réflexion ou un rebond ne réévalue pas une autre fonction aléatoire. Le profil dépend des secondes absolues et de l'identité structurelle ; pas du numéro de frame, du nombre d'appels, de l'ordre des lumières ou du niveau de qualité. La clock client `InWorldEllapsedMilliseconds` s'arrête quand le jeu est en pause.

Une source éteinte n'est pas ressuscitée par son profil. Les historiques de radiance devront être invalidés ou recalculés lorsque l'énergie change. Les caches de visibilité peuvent être réutilisés si géométrie, position et dimensions restent inchangées. Le changement de gain d'une lampe ne justifie pas un nouveau BVH.

## Réception

Temps identiques à 30/60/120/144 FPS, mêmes intensités dans vue directe et réflexion, flammes indépendantes, stabilité des sources non combustibles, extinction sans résidu, flash sans replay, changement de monde sans source héritée, joueur avec deux mains sans double comptage via `EntityPlayer.LightHsv`, lampe colorée et variante éteinte conservées.

**Observation client R00 :** le joueur est encore représenté par son agrégat moteur sans ajout séparé des mains ; aucun vacillement supplémentaire n'est appliqué à cet agrégat. Le traitement exact des mains et des particules est R02. L'observation n'est pas une implémentation d'ombres ni un rendu visible. La référence CPU limite explicitement ses lumières à des points et refuse un rayon de source surfacique non nul plutôt que le transformer silencieusement en point.
