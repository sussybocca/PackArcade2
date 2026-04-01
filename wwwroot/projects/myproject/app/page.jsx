'use client';

import { useState, useEffect, useCallback, useRef } from 'react';

export default function Home() {
    const [score, setScore] = useState(0);
    const [timeLeft, setTimeLeft] = useState(30);
    const [gameActive, setGameActive] = useState(false);
    const [targets, setTargets] = useState([]);
    const [highScore, setHighScore] = useState(0);
    const gameAreaRef = useRef(null);
    const gameLoopRef = useRef(null);
    const spawnIntervalRef = useRef(null);

    // Spawn a new target at random position
    const spawnTarget = useCallback(() => {
        if (!gameActive || !gameAreaRef.current) return;

        const area = gameAreaRef.current.getBoundingClientRect();
        const size = 50;
        const maxX = area.width - size;
        const maxY = area.height - size;

        const newTarget = {
            id: Date.now() + Math.random(),
            x: Math.random() * maxX,
            y: Math.random() * maxY,
            size: size,
        };

        setTargets(prev => [...prev, newTarget]);

        // Auto-remove target after 1.5 seconds if not clicked
        setTimeout(() => {
            setTargets(prev => prev.filter(t => t.id !== newTarget.id));
        }, 1500);
    }, [gameActive]);

    // Handle clicking on a target
    const handleTargetClick = (id) => {
        if (!gameActive) return;
        setScore(prev => prev + 1);
        setTargets(prev => prev.filter(t => t.id !== id));
    };

    // Start the game
    const startGame = () => {
        setScore(0);
        setTimeLeft(30);
        setTargets([]);
        setGameActive(true);
    };

    // Game timer
    useEffect(() => {
        if (!gameActive) return;

        const timer = setInterval(() => {
            setTimeLeft(prev => {
                if (prev <= 1) {
                    clearInterval(timer);
                    setGameActive(false);
                    if (score > highScore) setHighScore(score);
                    return 0;
                }
                return prev - 1;
            });
        }, 1000);

        return () => clearInterval(timer);
    }, [gameActive, score, highScore]);

    // Spawn targets at increasing rate
    useEffect(() => {
        if (!gameActive) return;

        let spawnDelay = 800; // ms between spawns
        const interval = setInterval(() => {
            spawnTarget();
            // Gradually increase spawn rate (decrease delay)
            if (spawnDelay > 300) {
                spawnDelay -= 10;
                clearInterval(interval);
                spawnIntervalRef.current = setInterval(spawnTarget, spawnDelay);
            }
        }, spawnDelay);

        spawnIntervalRef.current = interval;

        return () => clearInterval(interval);
    }, [gameActive, spawnTarget]);

    // Cleanup on unmount
    useEffect(() => {
        return () => {
            if (spawnIntervalRef.current) clearInterval(spawnIntervalRef.current);
            if (gameLoopRef.current) cancelAnimationFrame(gameLoopRef.current);
        };
    }, []);

    return (
        <div style={{ textAlign: 'center', padding: '2rem', minHeight: '100vh', background: '#0a0e1a', color: '#fff' }}>
            <h1 style={{ color: '#00ffaa' }}>🎯 TARGET CLICKER</h1>
            <div style={{ display: 'flex', justifyContent: 'center', gap: '2rem', marginBottom: '1rem' }}>
                <div>Score: <strong style={{ color: '#00ffaa' }}>{score}</strong></div>
                <div>Time: <strong style={{ color: '#ffaa44' }}>{timeLeft}s</strong></div>
                <div>🏆 High Score: <strong>{highScore}</strong></div>
            </div>

            {!gameActive && (
                <button
                    onClick={startGame}
                    style={{
                        padding: '12px 24px',
                        fontSize: '18px',
                        background: '#00ffaa',
                        color: '#0a0e1a',
                        border: 'none',
                        borderRadius: '8px',
                        cursor: 'pointer',
                        marginBottom: '20px',
                        fontWeight: 'bold'
                    }}
                >
                    {timeLeft === 0 ? 'PLAY AGAIN' : 'START GAME'}
                </button>
            )}

            <div
                ref={gameAreaRef}
                style={{
                    position: 'relative',
                    width: '100%',
                    maxWidth: '800px',
                    height: '500px',
                    margin: '0 auto',
                    background: '#1a1e2a',
                    borderRadius: '16px',
                    overflow: 'hidden',
                    cursor: 'crosshair',
                    border: '2px solid #2a2e3a',
                    boxShadow: '0 0 20px rgba(0,255,170,0.2)'
                }}
            >
                {targets.map(target => (
                    <div
                        key={target.id}
                        onClick={() => handleTargetClick(target.id)}
                        style={{
                            position: 'absolute',
                            left: target.x,
                            top: target.y,
                            width: target.size,
                            height: target.size,
                            background: 'radial-gradient(circle, #ff4444, #aa0000)',
                            borderRadius: '50%',
                            cursor: 'pointer',
                            boxShadow: '0 0 10px rgba(255,0,0,0.5)',
                            transition: 'transform 0.05s linear',
                            display: 'flex',
                            alignItems: 'center',
                            justifyContent: 'center',
                            fontSize: '24px'
                        }}
                    >
                        🎯
                    </div>
                ))}
                {!gameActive && timeLeft === 0 && (
                    <div style={{
                        position: 'absolute',
                        top: '50%',
                        left: '50%',
                        transform: 'translate(-50%, -50%)',
                        textAlign: 'center',
                        background: 'rgba(0,0,0,0.7)',
                        padding: '20px',
                        borderRadius: '12px'
                    }}>
                        <h2>Game Over!</h2>
                        <p>Final Score: {score}</p>
                        <p>High Score: {highScore}</p>
                    </div>
                )}
            </div>

            <div style={{ marginTop: '20px', fontSize: '14px', color: '#888' }}>
                Click on the red targets before they disappear! Speed increases over time.
            </div>
        </div>
    );
}