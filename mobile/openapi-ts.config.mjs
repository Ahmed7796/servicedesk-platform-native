// Generates the typed API clients from the contracts. Run: npm run gen:api
// Output is committed and must not be edited by hand (src/api/generated/README.md).
export default [
  {
    input: '../contracts/openapi/servicedesk-api.yaml',
    output: { path: 'src/api/generated/servicedesk', clean: true },
    plugins: ['@hey-api/typescript', '@hey-api/sdk', { name: '@hey-api/client-fetch', baseUrl: false }],
  },
  {
    input: '../contracts/openapi/notification-service.yaml',
    output: { path: 'src/api/generated/notification', clean: true },
    plugins: ['@hey-api/typescript', '@hey-api/sdk', { name: '@hey-api/client-fetch', baseUrl: false }],
  },
];
