<?php

// Merge this entry into the array in config/services.php (it is a separate file here only so
// you can see exactly what to add).

return [
    'receivify' => [
        'key' => env('RECEIVIFY_API_KEY'),
        'webhook_secret' => env('RECEIVIFY_WEBHOOK_SECRET'),
        'url' => env('RECEIVIFY_API_URL', 'https://receivify.io'),
    ],
];
