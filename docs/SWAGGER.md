# Swagger UI

The API exposes an interactive Swagger UI during local development.

## Start the API

From the repository root on Windows, run:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project PDVMottainai.Api
```

Open `http://localhost:5097/swagger` in a browser. The generated OpenAPI JSON is available at `http://localhost:5097/swagger/v1/swagger.json`.

Swagger endpoints are enabled only when the hosting environment is `Development`. The API itself continues to listen on loopback by default.

## Authentication

Use the **Authorize** button in Swagger UI:

- **FirebaseBearer**: provide a Firebase ID token to call `POST /auth/login` and manager approval decision routes.
- **PosBearer**: provide the local access token returned by `POST /auth/login` or `POST /auth/offline-login` to call protected POS operations.

Enter the token only; Swagger UI applies the `Bearer` prefix. Anonymous endpoints do not require a token. Keep real tokens and credentials private.

## Product lookup and database checks

`GET /products?query={barcode}` searches the active store's products. When PostgreSQL is configured and reachable, the API queries the operational database and may return promotion details for an eligible, verified customer. If PostgreSQL is unavailable, the API can return products from its local cache without promotions. A successful response alone therefore does not prove that PostgreSQL was reached; check the API logs while making the request.

The Swagger UI documents the API routes, path and query parameters, request bodies, response schemas, and the authorization token required by each operation.
