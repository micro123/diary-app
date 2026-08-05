# Local Redmine Test Server

Start the server from this directory:

```bash
docker compose up -d
```

Open http://localhost:3000.

The initial administrator account is created by Redmine on first access. The
default credentials are `admin` / `admin`; Redmine will require changing the
password after login.

Useful commands:

```bash
docker compose ps
docker compose logs -f redmine
docker compose down
```

The database and uploaded files are stored in Docker volumes. To remove all
test data as well as the containers:

```bash
docker compose down -v
```
