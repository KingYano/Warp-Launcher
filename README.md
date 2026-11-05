# Warp

Un lanceur de jeux pour Windows : tes jeux PC et émulés dans une seule fenêtre, avec leur visuel, ton temps de jeu, et la mise à jour de l'émulateur en un clic.

Warp ne contient **aucun jeu, aucune clé, aucun firmware**. Il lance les programmes que tu as déjà installés.

## Ce qu'il fait

- **Ta collection** : un jeu par carte, avec son visuel. On ajoute, modifie ou retire un jeu depuis l'app.
- **Jouer** : lance le jeu ou l'émulateur avec ses options (dossier de travail, variables d'environnement, arguments).
- **Jeu déjà ouvert** : le bouton devient « Revenir au jeu » et ramène sa fenêtre au premier plan.
- **Temps de jeu** et date de la dernière partie, pour chaque jeu.
- **Avertissement** avant de lancer un second jeu quand un premier tourne déjà (mémoire), et message si un jeu se ferme juste après son lancement.
- **Mises à jour d'émulateur depuis GitHub** : Warp suit les publications d'un dépôt, vérifie l'empreinte SHA-256 de l'archive, met l'ancienne version de côté et installe la nouvelle sans toucher au dossier `user` (clés, sauvegardes, réglages). Un bouton remet la version précédente.
- **Fenêtre à refermer** : si un programme pose la même question à chaque démarrage, Warp peut la refermer pour toi (champ `dismiss`).
- **Clavier et manette** : flèches et Entrée, ou croix/stick et A.

## Installer

1. Télécharge le zip de la dernière version dans [Releases](../../releases).
2. Décompresse-le où tu veux (évite le Bureau s'il est synchronisé avec OneDrive).
3. Lance `Warp.exe`, puis ajoute tes jeux avec la tuile **« + Ajouter un jeu »**.

Il faut Windows 10 ou 11 en 64 bits. Le composant Microsoft Edge WebView2 est déjà présent sur Windows 11 ; sur Windows 10, Warp indique où le télécharger s'il manque.

## La collection : `games.json`

Warp enregistre ta collection dans `games.json`, à côté de `Warp.exe`. Tout se règle depuis l'app ; le fichier peut aussi s'éditer à la main, Warp fermé. Exemple commenté : [`games.example.json`](games.example.json).

| Champ | Rôle |
|---|---|
| `name`, `subtitle` | Nom et petite ligne affichés |
| `exe`, `args` | Programme à lancer et ses options, comme dans un raccourci Windows |
| `cwd`, `env` | Dossier de travail et variables d'environnement (facultatifs) |
| `image` | Visuel, idéalement en 16:9 (chemin absolu ou relatif au dossier de Warp) |
| `update.repo`, `update.channel` | Dépôt GitHub de l'émulateur à suivre ; `versions` (numérotées) ou `builds` (tout) |
| `dismiss` | Titre exact d'une fenêtre à refermer au démarrage du jeu |

Avec un émulateur de la famille yuzu / Eden / Eglantine, `-g "chemin\du\jeu"` démarre directement dans le jeu. Pour un jeu en dossier (exefs + romfs), le chemin est celui du fichier `exefs\main`.

## Compiler

Aucun outil à installer : `src\build.bat` utilise le compilateur C# fourni avec Windows (.NET Framework 4.8) et produit `Warp.exe`.

- `src\Launcher.cs` : la fenêtre (WinForms + WebView2), le lancement et le suivi des jeux, la collection.
- `src\Updater.cs` : les mises à jour depuis GitHub.
- `ui\` : l'interface (HTML, CSS, JavaScript, polices).

## Licence

Warp est sous licence MIT (voir [LICENSE](LICENSE)). Les polices et les composants WebView2 ont leurs propres licences, dans [`licences/`](licences).
