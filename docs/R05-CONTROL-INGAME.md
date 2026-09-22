# R05 — commande globale et essais dans le client réel

Version : **0.3.3-dev.1**. Le lot conserve R04 (matériaux conducteurs), sur la même ligne de développement `dev/renderer-rewrite-20260921`. Base distante utilisée : `95d5798c`. Aucun nouveau backend, aucune nouvelle branche distante.

## Commandes

À saisir dans le chat du **client**, avec un point et non une barre oblique :

```text
.vrtx on
.vrtx off
.vrtx toggle
.vrtx status
.vrtx coverage
.vrtx retry
```

Les anciennes commandes `.vrtxworld` restent des alias de ce contrôle. `off` désactive le remplacement de l'image, masque le laboratoire, suspend les lectures du monde, la découverte des sources, leur modulation et les uploads de l'observateur. Les shaders restent installés et leur branche native est sélectionnée ; quelques callbacks et mises à zéro des uniformes subsistent, ce n'est pas un déchargement complet du mod. Les textures déjà allouées restent en cache. Une restauration de mode en fin de test ne masque pas une erreur du renderer ; la commande explicite de reprise conserve cette responsabilité. Aucune promesse de coût strictement identique à un client sans mod n'en découle.

Les notifications de modifications restent acceptées, avec des files bornées. En cas de saturation, le retour actif invalide les sources et la géométrie au lieu de garder une lampe dont le retrait aurait été perdu. Après `on`, une observation fraîche est obligatoire avant publication d'une nouvelle frame lumineuse. Une ancienne frame ne peut pas être réemployée silencieusement. Aucun rechargement systématique de shaders à chaque bascule.

L'état est conservé dans la session du client, **pas encore enregistré pour le prochain démarrage**. Le démarrage conserve la valeur active par défaut de R04. `status` distingue le mode demandé de celui appliqué au dernier passage opaque.

## Campagne dans le monde courant

```text
.vrtxtest start
.vrtxtest status
.vrtxtest abort
```

Utiliser un **monde de test** et se placer face à une surface éclairée, de préférence en intérieur ou de nuit. Fermer le chat et ne plus déplacer la caméra. La campagne ne place ni ne retire de blocs, ne téléporte pas le joueur, ne change pas l'heure, la météo ou les paramètres graphiques, n'écrit pas de commandes serveur et ne copie aucune authentification. Le jeu lui-même peut sauvegarder normalement le monde utilisé.

Séquence : préparation active, image native A1, image VintageRTX B, diagnostic de couverture, image native A2. Le protocole attend des frames où le mode demandé a réellement alimenté les programmes, pas simplement une réponse textuelle à une commande. Les captures sont lues sur le framebuffer par défaut au stade **AfterBlit**, avant les interfaces. Pas de reconstruction d'une image du monde à partir de la scène synthétique du laboratoire.

Chaque phase dispose d'un délai de stabilisation et d'un minimum de frames ; la campagne est bornée à 90 secondes de temps réel quand les callbacks de rendu sont exécutés. Une pause ne devient jamais une mesure de performances. Une sortie de monde, une interruption par commande ou un échec du rendu termine la campagne et restaure le mode précédent. Le nouveau choix explicite de l'utilisateur (`.vrtx off` par exemple) est ensuite prioritaire. Le laboratoire n'est pas réactivé automatiquement.

### Résultats et portée

Les fichiers sont écrits sous `GamePaths.Logs/VintageRTX/runtime/<date-identifiant>/` :

- `native-before.png`, `enabled.png`, `coverage.png`, `native-after.png` : résolution du viewport natif, octets RGBA8 d'affichage, aucun rééchantillonnage ou changement d'exposition ajouté.
- `runtime-inputs.json` : empreintes des DLL du mod, de son manifeste et de ses includes sur disque, ainsi que les identifiants MVID des modules chargés. Un chargement d'assembly depuis un flux peut ne pas fournir de chemin : la campagne manuelle conserve alors le MVID ; le lanceur strict refuse de conclure sans empreinte de DLL comparable. Ce n'est pas une attestation complète de tous les assets patchés en mémoire par tous les autres mods.
- `runtime-result.json` : état, raison détaillée, empreinte de chaque capture, caméra, mode demandé/appliqué, frames lumineuses, état des collecteurs et informations du pilote.

**PASS signifie uniquement que la bascule change réellement l'image et que la référence native est retrouvée.** Ce résultat ne certifie ni la photométrie, ni les ombres, ni la latence de pose d'une lampe, ni les reflets, l'eau ou la GI. Le laboratoire de rendu et ce contrôle d'intégration ne se substituent pas à leurs scénarios physiques futurs.

Une comparaison native A1/A2 instable, un mouvement de caméra, une projection/dimension différente, une modification de blocs ou un changement de résolution rendent la mesure `INCONCLUSIVE`. L'absence d'effet attribuable est aussi `INCONCLUSIVE`, jamais une réussite obtenue parce que les deux images sont restées natives. Les échantillons diagnostiques colorés ne sont pas interprétés comme des mesures physiques après le brouillard et la composition finale.

La différence d'image B est comparée au milieu des deux références A. Une dérive native moyenne supérieure à 2/255 invalide l'attribution. Il faut un effet moyen supérieur à `max(1/1024, 2*dérive + 1/1024)` et au moins 0,5 % de pixels réellement modifiés au-delà de la dérive. Ces seuils sont ceux d'un **smoke test d'intégration**, pas d'une référence d'aspect photoréaliste.

Les dt du moteur, sans les premières frames des phases et les opérations de capture/encodage, sont conservés comme informations contextuelles. Ils ne sont pas des timestamps GPU. Aucun seuil de FPS ni gain de performances n'est validé par ce protocole.

## Lanceur Windows

Depuis la racine du dépôt :

```powershell
powershell -NoProfile -File .\tools\runtime\Invoke-InGameToggleTest.ps1 `
    -VintageStoryPath 'C:\Chemin\Vers\Vintagestory' -Configuration Release
```

Fermer les autres instances du jeu avant le lancement. Le lanceur compile le mod (option `-NoBuild` pour reprendre une compilation existante), démarre le vrai client installé, puis laisse choisir un monde de test dans le menu normal. La campagne part après l'événement de disponibilité du joueur. Il faut retirer toute autre copie installée de VintageRTX pour ne pas charger deux versions.

Les variables `VINTAGERTX_RUNTIME_AUTOTEST=toggle` et `VINTAGERTX_RUNTIME_OUTPUT=<chemin absolu>` sont transmises **au seul nouveau processus** ; elles sont restaurées dans le processus lanceur immédiatement après le lancement. Aucune campagne ne part par défaut dans une session ordinaire. Les anciennes variables `VINTAGERTX_AUTO_CAPTURE` du profil historique ne déclenchent pas cette nouvelle campagne.

Le lanceur attend au maximum 300 secondes par défaut, contrôle l'identité de la DLL, les quatre captures et leurs empreintes, puis retourne 0 seulement pour le PASS du smoke test. Un autre résultat retourne un échec explicite avec le chemin du rapport. Il ne tue pas brutalement le client : sauvegarder et quitter le monde de test normalement. Aucun identifiant de connexion, mot de passe, token ou contenu de `clientsettings.json` n'est lu/copié par ce lanceur.

## Qualification du lot et limites d'exécution

Les tests de code emploient les DLL officielles de Vintage Story **1.22.7** et le SDK **10.0.401**. Ils vérifient l'arrêt effectif des lectures/uploads, la reprise sans source fantôme, les files bornées, la capture du framebuffer avec restauration des états GL, le codec PNG, les transitions et verdicts de la campagne, l'annulation, le retour du mode précédent et les erreurs de capture. Le rejeu complet du contrôleur est un **test à hôte contrôlé**, explicitement nommé ainsi, pas une partie jouée ; ses images artificielles sont supprimées et ne servent pas de preuves in-game.

Le lancement du client officiel a été tenté dans un répertoire de données neuf, isolé, avec la réécriture. L'aide CLI identifie bien la version 1.22.7. Le lancement de monde n'a cependant produit aucun rapport in-game : le processus termine sur une exception d'initialisation de `LoggerBase` dans le rapporteur de crash (code OS 134). Cette pile secondaire n'identifie pas à elle seule la cause initiale. Le jeu de fichiers local est un extrait de références, **pas une installation complète du client** (notamment pas de textures et pas d'apphost du jeu). Aucun contournement d'authentification n'a été tenté.

**Aucun PASS en condition réelle n'est donc revendiqué pour ce lot.** Les logs de tentative et les résultats locaux sont livrés séparément des futurs répertoires `runtime-result.json`. Le script PowerShell est fourni pour Windows ; il n'a pas été exécuté ici (PowerShell absent). Les tests du contrôleur C#/OpenGL ne constituent pas une validation de l'intégration du lanceur à Windows.

Références du contrat utilisé : API publique `ICoreClientAPI`, `IRenderAPI`, `EnumRenderStage.AfterBlit`, `IClientEventAPI`, vérifiées par compilation contre le jeu ciblé. Aucun nouvel assemblage du jeu n'est requis par R05.
