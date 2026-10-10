<?php

// Laravel 11+ bootstrap/app.php. The only Receivify-specific line is the CSRF exception.

use Illuminate\Foundation\Application;
use Illuminate\Foundation\Configuration\Exceptions;
use Illuminate\Foundation\Configuration\Middleware;

return Application::configure(basePath: dirname(__DIR__))
    ->withRouting(
        web: __DIR__.'/../routes/web.php',
        commands: __DIR__.'/../routes/console.php',
        health: '/up',
    )
    ->withMiddleware(function (Middleware $middleware) {
        // The webhook is a server-to-server POST with no CSRF token. It is authenticated by
        // its HMAC signature instead, which the controller verifies on every request.
        $middleware->validateCsrfTokens(except: ['webhooks/receivify']);
    })
    ->withExceptions(function (Exceptions $exceptions) {
        //
    })->create();
