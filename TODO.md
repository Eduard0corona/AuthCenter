# TODO

## Local runtime validation

- [ ] Configure real local user-secrets:
  - `ConnectionStrings:DefaultConnection`
  - `Jwt:SigningKey`
  - `Seed:AdminEmail`
  - `Seed:AdminPassword`
  - `Seed:AdminFullName`
- [ ] Run the API locally against SQL Server LocalDB:
  - Apply migrations.
  - Confirm the seed creates `AUTHCENTER`, default roles, default permissions, and the admin user.
  - Open Swagger at `https://localhost:7157/swagger`.
- [ ] Run a real auth smoke test against LocalDB:
  - `POST /api/auth/login`
  - `GET /api/auth/me`
  - `GET /api/users`
  - `POST /api/auth/refresh-token`

## Operational setup

- [ ] Add Docker Compose for the API and SQL Server.
- [ ] Prepare deployment configuration:
  - Choose hosting target.
  - Define production environment variables/secrets.
  - Review production CORS and health-check exposure.
