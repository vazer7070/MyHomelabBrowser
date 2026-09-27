# Serveur de support PommeBrowser

Petit serveur qui reçoit les rapports envoyés depuis le navigateur (menu › **Signaler un problème**) et les publie sur Discord. Les webhooks Discord restent sur ce serveur : ils ne sont jamais dans l'application distribuée.

Pour chaque rapport, le serveur :

1. vérifie le contenu : titre et description obligatoires, tailles limitées, caractères de contrôle retirés ;
2. applique une limite anti-abus : 5 rapports par adresse IP toutes les 10 minutes, 60 par heure au total ;
3. en garde une copie sur disque (`/data/reports/AAAA-MM/PB-…/report.json` et `log.txt`) ;
4. le publie dans le salon Discord de sa catégorie, avec le journal du navigateur en pièce jointe ;
5. renvoie au navigateur un identifiant (`PB-20260927-K7Q2XM`) affiché à l'utilisateur.

Si Discord est en panne, le rapport reste sur disque et l'utilisateur est quand même remercié. Si le serveur est injoignable, le navigateur enregistre le rapport en local, comme avant.

## 1. Créer les webhooks Discord

Dans Discord : **Paramètres du salon › Intégrations › Webhooks › Nouveau webhook › Copier l'URL du webhook**.

Un seul webhook suffit. Tu peux aussi en créer un par salon : bugs, demandes, interface…

> Les anciens webhooks étaient écrits en clair dans le code du navigateur et restent visibles dans l'historique Git. Supprime-les dans Discord et crée-en de nouveaux.

## 2. Configurer

```bash
cd support-server
cp .env.example .env
nano .env
```

Renseigne au moins `DISCORD_WEBHOOK_URL`. Les autres réglages sont expliqués dans le fichier :

| Variable | Rôle | Défaut |
|---|---|---|
| `DISCORD_WEBHOOK_URL` | Webhook utilisé par défaut | — |
| `DISCORD_WEBHOOK_URL_BUG`, `_MISSING_FEATURE`, `_FEATURE_REQUEST`, `_UI_UX`, `_PERFORMANCE`, `_OTHER` | Un salon par type de rapport (facultatif) | webhook par défaut |
| `SUPPORT_STORE_REPORTS` | Copie des rapports dans le volume `/data` | `true` |
| `SUPPORT_RETENTION_DAYS` | Suppression des copies après N jours (0 : jamais) | `90` |
| `SUPPORT_PROXY_HOPS` | Nombre de reverse proxies devant le serveur | `1` |
| `SUPPORT_BEHIND_CLOUDFLARE` | Lire l'adresse du visiteur dans `CF-Connecting-IP` | `false` |
| `SUPPORT_REPORTS_PER_ADDRESS` / `SUPPORT_ADDRESS_WINDOW_MINUTES` | Limite par adresse IP | `5` / `10` |
| `SUPPORT_REPORTS_PER_HOUR` | Limite globale | `60` |

Une adresse de webhook mal copiée empêche le serveur de démarrer, avec un message clair dans les journaux.

## 3. Lancer

```bash
docker compose up -d --build
docker compose logs -f
```

Au démarrage, les journaux indiquent les catégories reliées à Discord et le dossier de copie :

```
Serveur de support prêt. Discord : bug, missing_feature, feature_request, ui_ux, performance, other. Copie sur disque : /data.
```

L'image fonctionne sur x64 comme sur ARM (Raspberry Pi…). Le conteneur tourne sous un utilisateur non privilégié et Docker vérifie sa santé via `/health`.

## 4. Publier en HTTPS

Le navigateur doit joindre le serveur depuis Internet, en HTTPS. Choisis un sous-domaine, par exemple `support.mondomaine.fr`, et fais-le pointer vers le port 8080 du conteneur.

**Nginx Proxy Manager** : Proxy Hosts › Add Proxy Host.
- Domaine : `support.mondomaine.fr`.
- Forward : `http`, IP de la machine (ou `pommebrowser-support` si NPM est dans le même réseau Docker), port `8080`.
- Onglet SSL : certificat Let's Encrypt, cocher « Force SSL ».

**Caddy** :

```
support.mondomaine.fr {
    reverse_proxy 127.0.0.1:8080
}
```

**Traefik** (labels à ajouter au service dans `docker-compose.yml`, en retirant `ports`) :

```yaml
    labels:
      - traefik.enable=true
      - traefik.http.routers.pommebrowser-support.rule=Host(`support.mondomaine.fr`)
      - traefik.http.routers.pommebrowser-support.entrypoints=websecure
      - traefik.http.routers.pommebrowser-support.tls.certresolver=letsencrypt
      - traefik.http.services.pommebrowser-support.loadbalancer.server.port=8080
```

**Cloudflare Tunnel** : Public Hostname `support.mondomaine.fr` vers `http://localhost:8080`. Mets `SUPPORT_BEHIND_CLOUDFLARE=true` dans `.env`. Si le tunnel passe aussi par un reverse proxy, garde `SUPPORT_PROXY_HOPS=1`.

`SUPPORT_PROXY_HOPS` doit correspondre au nombre de proxies réellement placés devant le serveur. Sinon, tous les visiteurs partagent la même limite anti-abus.

## 5. Vérifier

```bash
curl https://support.mondomaine.fr/health
# ok

curl -F 'report={"title":"Test","description":"Rapport de test","category":"other"};type=application/json' \
     https://support.mondomaine.fr/api/v1/support/reports
# {"accepted":true,"reportId":"PB-…","message":"Rapport reçu. Merci !"}
```

Le message de test doit apparaître dans Discord.

## 6. Relier le navigateur

Dans `MyHomelabBrowser.csproj`, écris l'adresse du serveur sur la seconde ligne `SupportApiUrl` :

```xml
<SupportApiUrl Condition="'$(SupportApiUrl)' == ''">$(POMMEBROWSER_SUPPORT_API_URL)</SupportApiUrl>
<SupportApiUrl Condition="'$(SupportApiUrl)' == ''">https://support.mondomaine.fr</SupportApiUrl>
```

Autre possibilité : définir la variable d'environnement `POMMEBROWSER_SUPPORT_API_URL` avant de compiler ou de lancer `build-pack-velopack.ps1`.

Recompile, puis envoie un rapport depuis le navigateur : la boîte « Message envoyé » affiche l'identifiant du rapport.

Pour tester un autre serveur sans recompiler, lance le navigateur avec la variable `POMMEBROWSER_SUPPORT_API_URL`.

## Mettre à jour

```bash
git pull
docker compose up -d --build
```

## Consulter les rapports enregistrés

```bash
docker compose exec pommebrowser-support ls -R /data/reports
docker compose cp pommebrowser-support:/data/reports ./rapports
```

## Développement

```bash
dotnet run --project src/PommeBrowser.SupportServer        # http://localhost:5000
dotnet test tests/PommeBrowser.SupportServer.Tests
```

Les tests couvrent :
- l'envoi vers Discord et le choix du salon ;
- la copie sur disque et la conservation ;
- les refus (champs vides, taille, format) et les limites anti-abus, derrière un proxy ou Cloudflare ;
- la réponse dans la langue du navigateur.

Ils font aussi passer le code d'envoi du navigateur (`SupportApiTransport`) par ce serveur, pour que les deux côtés restent compatibles.
