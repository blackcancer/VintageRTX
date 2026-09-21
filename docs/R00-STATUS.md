# État de R00 : fondations de la réécriture

La réécriture est sur `dev/renderer-rewrite-20260921`, issue de `1a82a0ca6f8a7566c19f56cfa7852312764db755`. La branche recovery et main ne sont pas modifiées. Le premier commit du nouveau code est `7ef48944c7286e6bcf0bf3956cc8acf4743addbc`.

## Vérification déjà exécutée

Run GitHub Actions `35577996549` : les **49 tests du cœur** sont exécutés et réussis, aucun ignoré. L'adaptateur est compilé contre les DLL du client officiel Vintage Story **1.22.7**, SDK **10.0.401**. Les deux jobs ont terminé avec succès. Ces résultats concernent ce premier commit précis et ne certifient pas automatiquement les commits suivants.

Les tests portent sur le registre lumineux, la modulation déterministe, les mises à jour régionales indépendantes, l'ordonnanceur, les intersections/BVH, l'albedo linéaire et le transport de référence diffuse/conducteur. Les 1 000 comparaisons BVH/exhaustif qualifient la traversée ; l'intersection du triangle possède en plus un oracle de plan analytique. Ce n'est pas encore une preuve de robustesse sur toutes les géométries dégénérées ou les données de meshes tiers.

## Complément optique de ce commit

Ajout des directions de réflexion/réfraction d'une interface lisse, du transport Beer-Lambert et d'un miroir autour d'un plan. Huit tests supplémentaires. L'orientation de la normale vers le milieu incident et les deux indices sont explicites : pas de déduction du milieu depuis la seule hauteur de caméra. La transmission est absente lors d'une réflexion interne totale.

La revue a identifié et corrigé le cas limite d'indices identiques à incidence exactement rasante : il n'existe alors pas d'interface optique. La limite sans absorption de l'indice complexe utilise le calcul diélectrique et rejette les paramètres invalides. Les nouveaux résultats doivent être consultés dans le run de ce commit.

Source physique de référence : https://www.pbr-book.org/4ed/Reflection_Models/Specular_Reflection_and_Transmission

## Ce que cette branche ne fait PAS encore

Aucun nouveau dessin GPU dans le monde. Pas encore de mesh-acquisition complète, pas de textures PBR capturées par la réécriture, pas d'ombre ou de reflet de jeu, pas de pilotage météo de la foudre et pas d'attache osseuse des lampes. Le module client est un observateur via l'API publique ; la commande `.vrtxrewrite` indique cet état sans annoncer une fonctionnalité active.

Les matériaux et le tracer CPU sont une référence contrôlée. L'indice conducteur de test n'est pas présenté comme un jeu de mesures de cuivre. Le tracer conserve des émetteurs ponctuels, rejette explicitement les émetteurs surfaciques non pris en charge et utilise un nombre borné de rebonds dans une scène fournie complète. Les fonctions de réfraction sont testées séparément ; elles ne sont pas encore intégrées à ce tracer multirebond.

Les intensités de l'observateur sont relatives et provisoires. Les profils de flamme sont artistiques, déterministes et bornés, non des spectres mesurés. Les locustes restent pilotés par leur `LightHsv` réel ; le joueur conserve provisoirement l'agrégat moteur de ses mains sans double comptage. Le raccordement exact des mains/flammes/réflexions dans le jeu reste R02.

## Première réception visuelle suivante

R01 : acquisition de la vraie géométrie et des matériaux, transfert GPU par région, lumière directe et visibilité locale. Validation d'une lampe posée/retirée sans attente des caches solaire/pluie/GI, avec mesure complète du délai événement -> observation -> publication -> image. Le menu Vintage Story, les surfaces eau/verre, les instances animées, les reflets hors écran et la GI temporelle restent dans le périmètre de la réécriture, pas supprimés pour atteindre un premier résultat.
