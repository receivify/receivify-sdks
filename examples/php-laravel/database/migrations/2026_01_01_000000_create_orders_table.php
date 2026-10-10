<?php

use Illuminate\Database\Migrations\Migration;
use Illuminate\Database\Schema\Blueprint;
use Illuminate\Support\Facades\Schema;

return new class extends Migration
{
    public function up(): void
    {
        Schema::create('orders', function (Blueprint $table) {
            $table->string('id')->primary();       // also the Receivify invoice `reference`
            $table->string('product');
            $table->decimal('amount', 12, 2);      // a decimal column, never a float
            $table->string('status')->default('pending');
            $table->string('invoice_id')->nullable();
            $table->unsignedBigInteger('last_seq')->default(0); // highest webhook sequence applied
            $table->timestamps();
        });
    }

    public function down(): void
    {
        Schema::dropIfExists('orders');
    }
};
