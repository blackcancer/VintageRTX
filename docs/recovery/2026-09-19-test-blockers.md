# Blocages de tests — corrections et exécution du 19 septembre 2026

## État livré

Branche : `dev/renderer-recovery-20260918`. Sources corrigées : `b3559ebf42c09559b3298dd2f6dc5009fff1cc9b`.
`main` n'a pas été modifiée. Ce lot répare les blocages de validation ; ce n'est pas une certification du rendu en jeu ni la clôture des défauts visuels du premier essai.

## Corrections

- **Projection miroir singulière.** L'inversibilité est vérifiée avant toute opération GL. Les coefficients arrondis en float utilisés par le shader servent aussi au calcul de l'inverse. Une projection oblique invalide revient à une paire ordinaire valide ; sans paire valide, seule la passe miroir est refusée et journalisée, sans rendre tout le renderer définitivement fautif. Aucun inverse identité n'est fabriqué pour une profondeur incompatible. Quatre régressions vérifient l'aller-retour, le mauvais inverse, les données non finies et l'arrondi float.
- **Démarrage/fermeture.** Le dialogue est construit à la demande, pas pendant le bootstrap. Les API GUI/input absentes pendant un démarrage partiel sont gérées explicitement. Le test de bootstrap inclut la commande settings et les raccourcis ; une ouverture sans GUI retourne une erreur de commande.
- **Contrats de shaders.** Les tests textuels normalisent LF/CRLF. Le test DDA examine les noyaux effectifs plutôt que compter cinq copies d'une ancienne expression. Les attentes qui exigeaient une dilatation des silhouettes, une visibilité inconditionnelle des lumières tenues ou des budgets de diagnostic augmentés ont été remplacées par les contrats corrigés. Les assertions numériques existantes de sélection stable et d'énergie restent en place. Les directions diagonales sont aussi exécutées dans le GLSL de production contre un oracle rayon/boîtes indépendant.
- **Fixture réelle.** Le PNG original n'a pas changé. Lecture par `File.ReadAllBytes`, vérification SHA-256 puis décodage Skia des octets. Un test copie ces mêmes octets dans un chemin accentué et vérifie aussi le rejet d'une corruption. Cela supprime le passage du chemin au décodeur natif, sans prétendre que l'origine exacte du problème Windows est démontrée.
- **Scénario toit.** Le faux monde distingue désormais les blocs de toiture du sol. Soleil vertical ou calendrier absent ne peuvent pas fournir le témoin d'ombre déportée extérieure exigé par ce scénario ; ces cas sont refusés sans téléportation. Le cas positif à soleil incliné doit toujours réussir. La physique des ombres du renderer n'a pas été modifiée par ce changement du scénario.
- **Tests multiplateformes.** Détection du client Linux et copie conditionnelle des bibliothèques natives ; pas de fichier `.exe` Windows exigé sur Linux pour les tests d'assets.

## Preuves exécutées

GitHub Actions : run `35406701683`, job `105797827861`. Le checkout de préparation `e6f07d3d32381f010cbb2c76f42e8ff040becd6e` a été transformé, compilé et testé, puis les fichiers ordinaires ont été publiés dans `b3559ebf42c09559b3298dd2f6dc5009fff1cc9b` uniquement après réussite.

Environnement : client officiel Vintage Story 1.22.7 Linux, SDK .NET 10.0.401, Ubuntu 24.04.5, Mesa llvmpipe LLVM 20.1.2, Xvfb/EGL.

| Contrôle | Résultat |
|---|---|
| Compilation Release du mod et du projet MSTest | Réussite, 0 erreur, 3 avertissements |
| Campagne ciblée MSTest | 35 exécutés, 35 réussis, 0 ignoré/inconclusif |
| Régression `PreFinalDisplayPassUsesRealHiddenContextAndRestoresState` | Réussite dans un vrai contexte GL caché |
| GLSL `tests/recovery` | 4/4, dont 256 rayons et liaison du shader complet |
| GLSL `tests/feedback` | 4/4, dont visibilité de l'eau, métadonnées et contacts diagonaux |
| Intégrité du PNG réel | 8 507 octets, 960 × 505, CRC et décompression valides |
| Scénarios dans un monde Vintage Story | Non exécutés ici |
| Validation Windows / GPU du joueur | À effectuer |

SHA-256 du PNG réel : `40cbff450c6f9a8d8b8082f2cc712a0d4f168e9ab5a08a364f59e8fa8aa8dd3e`.
Artefact de preuve : `test-blockers-e6f07d3d32381f010cbb2c76f42e8ff040becd6e`, ID `10573055577`.
Les trois avertissements concernent deux nullabilités (`EntityLightCollector`, `RuntimeScenarioProbe`) et un `cref` de documentation dans `FilmicDisplayRenderer` ; ils ne sont pas cachés.

Le workflow ponctuel de publication est retiré. La CI ordinaire, en lecture seule, compile les sources déjà commitées, exécute la campagne partagée ci-dessous et conserve les tests GLSL. Son contrôle du TRX refuse un résultat vide, incomplet ou ignoré. Les anciennes recettes sous `tools/recovery` sont des traces de développement : ne pas les exécuter sur la branche corrigée.

## Reprise courte sous Windows

Conserver les modifications locales, puis récupérer la branche et vérifier le commit. `VINTAGE_STORY` doit pointer vers l'installation 1.22.7 utilisée pour les essais.

```powershell
git fetch origin
git switch dev/renderer-recovery-20260918
git pull --ff-only
git log -1 --oneline

dotnet restore .\tests\VintageRTX.Test\VintageRTX.Test.csproj --source https://api.nuget.org/v3/index.json
dotnet test .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release --no-restore --settings .\tests\blocker-smoke.runsettings --logger "trx;LogFileName=blocker-smoke.trx"
```

Cette commande recompile avant les tests ; elle ne lance pas les scénarios longs du jeu. Dans Visual Studio, le fichier `tests/blocker-smoke.runsettings` peut être sélectionné comme fichier de paramètres de test.

Après réussite, reprendre un seul scénario `lantern-night`, puis étendre la campagne. Un code de sortie 1 commun n'établit pas une cause commune : en cas de nouvel échec, conserver le TRX et le dossier d'artefacts de ce scénario (notamment les logs client), plutôt que relancer immédiatement tous les scénarios.
